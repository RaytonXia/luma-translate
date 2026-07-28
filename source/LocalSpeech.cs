using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using WinFoundation = Windows.Foundation;
using WinSpeech = Windows.Media.SpeechSynthesis;
using WinStreams = Windows.Storage.Streams;

namespace SGFloatingTranslator
{
    internal sealed class LocalSpeechVoice
    {
        internal string Id { get; private set; }
        internal string DisplayName { get; private set; }
        internal string Description { get; private set; }
        internal string Language { get; private set; }
        internal string Gender { get; private set; }

        internal LocalSpeechVoice(
            string id,
            string displayName,
            string description,
            string language,
            string gender)
        {
            Id = id ?? String.Empty;
            DisplayName = displayName ?? String.Empty;
            Description = description ?? String.Empty;
            Language = language ?? String.Empty;
            Gender = gender ?? String.Empty;
        }

        public override string ToString()
        {
            string genderLabel = String.Equals(Gender, "Female", StringComparison.OrdinalIgnoreCase)
                ? "女声"
                : String.Equals(Gender, "Male", StringComparison.OrdinalIgnoreCase) ? "男声" : String.Empty;
            return DisplayName + " · " + Language +
                (String.IsNullOrEmpty(genderLabel) ? String.Empty : " · " + genderLabel);
        }
    }

    internal sealed class LocalSpeechCompletedEventArgs : EventArgs
    {
        internal Exception Error { get; private set; }
        internal bool Cancelled { get; private set; }

        internal LocalSpeechCompletedEventArgs(Exception error, bool cancelled)
        {
            Error = error;
            Cancelled = cancelled;
        }
    }

    // Uses Windows' modern OneCore speech voices. Synthesis and playback remain fully local.
    internal sealed class LocalEnglishSpeech : IDisposable
    {
        private readonly WinSpeech.SpeechSynthesizer synthesizer;
        private readonly List<LocalSpeechVoice> voices;
        private readonly Dictionary<string, WinSpeech.VoiceInformation> voiceMap;
        private readonly object syncRoot;
        private CancellationTokenSource cancellation;
        private SoundPlayer player;
        private MemoryStream audioStream;
        private int generation;
        private bool disposed;

        internal event EventHandler SpeechStarted;
        internal event EventHandler<LocalSpeechCompletedEventArgs> SpeechCompleted;

        internal IList<LocalSpeechVoice> Voices { get { return voices.AsReadOnly(); } }
        internal LocalSpeechVoice SelectedVoice { get; private set; }
        internal bool IsSpeaking { get; private set; }

        internal LocalEnglishSpeech(string preferredVoiceId)
        {
            syncRoot = new object();
            voices = new List<LocalSpeechVoice>();
            voiceMap = new Dictionary<string, WinSpeech.VoiceInformation>(StringComparer.OrdinalIgnoreCase);
            synthesizer = new WinSpeech.SpeechSynthesizer();

            foreach (WinSpeech.VoiceInformation voice in WinSpeech.SpeechSynthesizer.AllVoices)
            {
                if (voice == null || String.IsNullOrWhiteSpace(voice.Language) ||
                    !voice.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                    continue;

                LocalSpeechVoice item = new LocalSpeechVoice(
                    voice.Id,
                    voice.DisplayName,
                    voice.Description,
                    voice.Language,
                    voice.Gender.ToString());
                voices.Add(item);
                voiceMap[item.Id] = voice;
            }

            voices.Sort(delegate(LocalSpeechVoice left, LocalSpeechVoice right)
            {
                int scoreComparison = VoiceScore(right).CompareTo(VoiceScore(left));
                return scoreComparison != 0
                    ? scoreComparison
                    : String.Compare(left.DisplayName, right.DisplayName, StringComparison.CurrentCultureIgnoreCase);
            });

            if (voices.Count == 0)
                throw new InvalidOperationException("No local Windows English voice is installed.");

            LocalSpeechVoice selected = FindVoice(preferredVoiceId);
            if (selected == null)
                selected = ChooseDefaultVoice();
            SelectVoice(selected.Id);
        }

        internal bool SelectVoice(string voiceId)
        {
            if (disposed || String.IsNullOrWhiteSpace(voiceId)) return false;
            LocalSpeechVoice selected = FindVoice(voiceId);
            WinSpeech.VoiceInformation voice;
            if (selected == null || !voiceMap.TryGetValue(selected.Id, out voice)) return false;
            try
            {
                synthesizer.Voice = voice;
                SelectedVoice = selected;
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal void SpeakAsync(string text, bool slow)
        {
            if (disposed) throw new ObjectDisposedException("LocalEnglishSpeech");
            if (String.IsNullOrWhiteSpace(text)) return;

            Stop();
            CancellationTokenSource current = new CancellationTokenSource();
            int currentGeneration;
            lock (syncRoot)
            {
                cancellation = current;
                currentGeneration = ++generation;
            }
            RunSpeechAsync(text, slow, current, currentGeneration);
        }

        internal void Stop()
        {
            CancellationTokenSource previous;
            SoundPlayer activePlayer;
            lock (syncRoot)
            {
                generation++;
                previous = cancellation;
                cancellation = null;
                activePlayer = player;
                player = null;
                IsSpeaking = false;
            }
            if (previous != null)
            {
                try { previous.Cancel(); } catch { }
                previous.Dispose();
            }
            if (activePlayer != null)
            {
                try { activePlayer.Stop(); } catch { }
                activePlayer.Dispose();
            }
            DisposeAudioStream();
        }

        private async void RunSpeechAsync(
            string text,
            bool slow,
            CancellationTokenSource current,
            int currentGeneration)
        {
            Exception failure = null;
            bool cancelled = false;
            try
            {
                synthesizer.Options.SpeakingRate = slow ? 0.78D : 0.96D;
                synthesizer.Options.AudioPitch = 1D;
                synthesizer.Options.AudioVolume = 1D;

                WinSpeech.SpeechSynthesisStream stream = await ToTask(
                    synthesizer.SynthesizeTextToStreamAsync(text),
                    current.Token);
                using (stream)
                {
                    byte[] audio = await ReadAllBytesAsync(stream, current.Token);
                    current.Token.ThrowIfCancellationRequested();

                    MemoryStream memory = new MemoryStream(audio, false);
                    SoundPlayer soundPlayer = new SoundPlayer(memory);
                    soundPlayer.Load();
                    lock (syncRoot)
                    {
                        if (disposed || currentGeneration != generation)
                        {
                            soundPlayer.Dispose();
                            memory.Dispose();
                            return;
                        }
                        audioStream = memory;
                        player = soundPlayer;
                        IsSpeaking = true;
                    }

                    EventHandler started = SpeechStarted;
                    if (started != null) started(this, EventArgs.Empty);
                    await Task.Run(delegate { soundPlayer.PlaySync(); }, current.Token);
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                bool notify;
                lock (syncRoot)
                {
                    notify = !disposed && currentGeneration == generation;
                    if (notify)
                    {
                        cancellation = null;
                        IsSpeaking = false;
                        if (player != null)
                        {
                            player.Dispose();
                            player = null;
                        }
                    }
                }
                if (notify)
                {
                    DisposeAudioStream();
                    current.Dispose();
                    EventHandler<LocalSpeechCompletedEventArgs> completed = SpeechCompleted;
                    if (completed != null)
                        completed(this, new LocalSpeechCompletedEventArgs(failure, cancelled));
                }
            }
        }

        private LocalSpeechVoice ChooseDefaultVoice()
        {
            // The list is ordered by locality and perceived-naturalness hints. A saved choice
            // always wins; otherwise a newer Natural/HD voice wins when Windows exposes one.
            return voices[0];
        }

        private LocalSpeechVoice FindVoice(string voiceId)
        {
            if (String.IsNullOrWhiteSpace(voiceId)) return null;
            foreach (LocalSpeechVoice voice in voices)
            {
                if (String.Equals(voice.Id, voiceId, StringComparison.OrdinalIgnoreCase))
                    return voice;
            }
            return null;
        }

        private static int VoiceScore(LocalSpeechVoice voice)
        {
            if (voice == null) return 0;
            int score = 0;
            if (voice.Language.StartsWith("en-SG", StringComparison.OrdinalIgnoreCase)) score += 500;
            else if (voice.Language.StartsWith("en-GB", StringComparison.OrdinalIgnoreCase)) score += 400;
            else if (voice.Language.StartsWith("en-US", StringComparison.OrdinalIgnoreCase)) score += 300;
            else score += 200;
            string searchable = (voice.DisplayName + " " + voice.Description).ToLowerInvariant();
            if (searchable.Contains("natural") || searchable.Contains("neural") ||
                searchable.Contains("enhanced") || searchable.Contains(" hd"))
                score += 5000;
            string[] modernNames = new string[]
            {
                "ava", "andrew", "jenny", "aria", "guy", "sonia", "ryan",
                "libby", "natasha", "william"
            };
            foreach (string name in modernNames)
            {
                if (!searchable.Contains(name)) continue;
                score += 1500;
                break;
            }
            // Mark is a OneCore voice that older System.Speech enumeration does not expose.
            if (searchable.Contains("mark")) score += 300;
            return score;
        }

        private static async Task<byte[]> ReadAllBytesAsync(
            WinSpeech.SpeechSynthesisStream stream,
            CancellationToken cancellationToken)
        {
            if (stream == null || stream.Size == 0) return new byte[0];
            if (stream.Size > Int32.MaxValue)
                throw new InvalidOperationException("Synthesized audio is too large.");
            uint length = checked((uint)stream.Size);
            using (WinStreams.IInputStream input = stream.GetInputStreamAt(0))
            using (WinStreams.DataReader reader = new WinStreams.DataReader(input))
            {
                uint loaded = await ToTask(reader.LoadAsync(length), cancellationToken);
                if (loaded == 0) return new byte[0];
                byte[] bytes = new byte[loaded];
                reader.ReadBytes(bytes);
                return bytes;
            }
        }

        private static Task<T> ToTask<T>(
            WinFoundation.IAsyncOperation<T> operation,
            CancellationToken cancellationToken)
        {
            TaskCompletionSource<T> completion = new TaskCompletionSource<T>();
            CancellationTokenRegistration registration = cancellationToken.Register(delegate
            {
                try { operation.Cancel(); } catch { }
                completion.TrySetCanceled();
            });
            operation.Completed = delegate(WinFoundation.IAsyncOperation<T> sender, WinFoundation.AsyncStatus status)
            {
                try
                {
                    if (status == WinFoundation.AsyncStatus.Completed)
                        completion.TrySetResult(sender.GetResults());
                    else if (status == WinFoundation.AsyncStatus.Canceled)
                        completion.TrySetCanceled();
                    else
                        completion.TrySetException(
                            sender.ErrorCode ?? new InvalidOperationException("Windows speech operation failed."));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
                finally
                {
                    registration.Dispose();
                }
            };
            return completion.Task;
        }

        private void DisposeAudioStream()
        {
            MemoryStream previous;
            lock (syncRoot)
            {
                previous = audioStream;
                audioStream = null;
            }
            if (previous != null) previous.Dispose();
        }

        public void Dispose()
        {
            if (disposed) return;
            Stop();
            disposed = true;
            synthesizer.Dispose();
        }
    }
}
