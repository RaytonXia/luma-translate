using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WinFoundation = Windows.Foundation;

namespace SGFloatingTranslator
{
    /// <summary>A locally recognized English word and the OCR line containing it.</summary>
    public sealed class OcrHit
    {
        public string Word { get; private set; }
        public string LineText { get; private set; }
        public IList<string> LineWords { get; private set; }
        public int WordIndex { get; private set; }
        public Point ScreenPoint { get; private set; }

        public OcrHit(string word, string lineText, IList<string> lineWords, int wordIndex, Point screenPoint)
        {
            Word = word ?? String.Empty;
            LineText = lineText ?? String.Empty;
            LineWords = lineWords == null
                ? new List<string>().AsReadOnly()
                : new List<string>(lineWords).AsReadOnly();
            WordIndex = wordIndex;
            ScreenPoint = screenPoint;
        }
    }

    public sealed class OcrHitEventArgs : EventArgs
    {
        public OcrHit Hit { get; private set; }

        public OcrHitEventArgs(OcrHit hit)
        {
            if (hit == null) throw new ArgumentNullException("hit");
            Hit = hit;
        }
    }

    public class OcrPointEventArgs : EventArgs
    {
        public Point ScreenPoint { get; private set; }

        public OcrPointEventArgs(Point screenPoint)
        {
            ScreenPoint = screenPoint;
        }
    }

    public sealed class OcrFailureEventArgs : OcrPointEventArgs
    {
        public string Message { get; private set; }

        public OcrFailureEventArgs(string message, Point screenPoint)
            : base(screenPoint)
        {
            Message = message ?? String.Empty;
        }
    }

    /// <summary>English text recognized from a user-dragged screen rectangle.</summary>
    public sealed class OcrSelectionEventArgs : OcrPointEventArgs
    {
        public string Text { get; private set; }
        public Rectangle ScreenBounds { get; private set; }

        public OcrSelectionEventArgs(string text, Rectangle screenBounds, Point screenPoint)
            : base(screenPoint)
        {
            Text = text ?? String.Empty;
            ScreenBounds = screenBounds;
        }
    }

    internal enum CursorBadgeState
    {
        Ready,
        AwaitSecondClick,
        SelectingAi,
        Processing
    }

    /// <summary>
    /// Turns the pointer into a translation cursor and recognizes the English word under a click.
    /// All OCR is performed by the installed Windows OCR language pack; this class has no network code.
    /// </summary>
    public sealed class TranslationMouseController : IDisposable
    {
        private const int WhMouseLl = 14;
        private const int WmMouseMove = 0x0200;
        private const int WmLButtonDown = 0x0201;
        private const int WmRButtonDown = 0x0204;
        private const int WmRButtonUp = 0x0205;
        private const int LongPressMilliseconds = 420;
        private const uint GaRoot = 2;
        private const int VkShift = 0x10;
        private const int VkControl = 0x11;
        private const int VkMenu = 0x12;
        private const int VkLWin = 0x5B;
        private const int VkRWin = 0x5C;
        private static readonly UIntPtr ReplayedRightClickMarker = new UIntPtr(0x4C554D41u); // "LUMA"
        private readonly Control owner;
        private readonly uint ownProcessId;
        private readonly LowLevelMouseProc hookProcedure;
        private readonly object gestureLock = new object();
        private IntPtr hookHandle;
        private volatile bool enabled;
        private volatile bool disposed;
        private volatile bool aiLongSentenceEnabled;
        private RightGestureState rightGestureState;
        private Point firstRightPoint;
        private Point latestRightPoint;
        private uint firstRightDownTime;
        private uint firstRightUpTime;
        private IntPtr firstRightTarget;
        private bool longPressArmed;
        private int gestureGeneration;
        private System.Threading.Timer singleRightTimer;
        private System.Threading.Timer longPressTimer;
        private TranslationCursorBadge cursorBadge;
        private AiSelectionOverlay selectionOverlay;
        private CancellationTokenSource recognitionCancellation;
        private int recognitionGeneration;
        private OcrEngine ocrEngine;
        private string requestedLanguageTag;
        private string resolvedLanguageTag;

        public event EventHandler<OcrHitEventArgs> Recognized;
        public event EventHandler<OcrSelectionEventArgs> SelectionRecognized;
        public event EventHandler<OcrFailureEventArgs> Failed;
        public event EventHandler<OcrPointEventArgs> Processing;
        public event EventHandler EnabledChanged;

        /// <summary>
        /// An observed (never swallowed) left button press. The host uses it to dismiss
        /// the dictionary bubble when the user clicks anywhere outside it.
        /// </summary>
        public event EventHandler<OcrPointEventArgs> LeftPressed;

        public bool Enabled
        {
            get { return enabled; }
        }

        /// <summary>
        /// Enables the right-hold-and-drag sentence gesture. The host sets this only when the
        /// preferred AI provider has both a key and explicit destination consent.
        /// </summary>
        public bool AiLongSentenceEnabled
        {
            get { return aiLongSentenceEnabled; }
            set { aiLongSentenceEnabled = value; }
        }

        /// <summary>
        /// Preferred OCR language. Set this before enabling. If unavailable, another installed English
        /// Windows OCR language is used. After enabling, the getter returns the resolved language tag.
        /// </summary>
        public string OcrLanguageTag
        {
            get
            {
                return String.IsNullOrWhiteSpace(resolvedLanguageTag)
                    ? (requestedLanguageTag ?? String.Empty)
                    : resolvedLanguageTag;
            }
            set
            {
                ThrowIfDisposed();
                if (enabled) throw new InvalidOperationException("Disable mouse translation before changing the OCR language.");
                requestedLanguageTag = value == null ? String.Empty : value.Trim();
                resolvedLanguageTag = null;
                ocrEngine = null;
            }
        }

        public TranslationMouseController(Control owner)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            this.owner = owner;
            using (Process process = Process.GetCurrentProcess())
            {
                ownProcessId = unchecked((uint)process.Id);
            }
            hookProcedure = HookCallback;
        }

        public bool TryEnable(out string error)
        {
            error = String.Empty;
            if (disposed)
            {
                error = "鼠标翻译控制器已经关闭。 / Mouse translation controller is disposed.";
                return false;
            }
            if (enabled) return true;
            if (owner.IsDisposed || owner.Disposing || !owner.IsHandleCreated)
            {
                error = "主窗口尚未就绪，无法开启鼠标翻译。 / The owner window is not ready.";
                return false;
            }
            if (owner.InvokeRequired)
            {
                error = "请在界面线程开启鼠标翻译。 / Enable mouse translation on the UI thread.";
                return false;
            }

            if (ocrEngine == null && !TryCreateEnglishOcrEngine(out ocrEngine, out resolvedLanguageTag, out error))
            {
                return false;
            }

            TranslationCursorBadge pendingCursorBadge = null;
            IntPtr pendingHook = IntPtr.Zero;
            try
            {
                // Preserve the user's real Arrow/I-beam/Hand/Wait shapes and hotspots. The
                // light animated badge communicates translation state without changing any
                // system-wide cursor resource.
                pendingCursorBadge = new TranslationCursorBadge();
                pendingCursorBadge.StartBadge();
                pendingHook = SetWindowsHookEx(WhMouseLl, hookProcedure, GetModuleHandle(null), 0);
                if (pendingHook == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to install the mouse hook.");
                }

                cursorBadge = pendingCursorBadge;
                pendingCursorBadge = null;
                hookHandle = pendingHook;
                pendingHook = IntPtr.Zero;
                ResetRightGesture(false);
                enabled = true;
                RaiseEnabledChanged();
                return true;
            }
            catch (Exception ex)
            {
                if (pendingHook != IntPtr.Zero) UnhookWindowsHookEx(pendingHook);
                if (pendingCursorBadge != null) pendingCursorBadge.Dispose();
                error = "无法开启鼠标即时翻译。 / Could not enable click translation. " + ex.Message;
                return false;
            }
        }

        public void SetEnabled(bool value)
        {
            ThrowIfDisposed();
            if (value)
            {
                string error;
                if (!TryEnable(out error)) RaiseFailed(error, Cursor.Position);
                return;
            }

            DisableCore(true);
        }

        public void Toggle()
        {
            SetEnabled(!Enabled);
        }

        private void DisableCore(bool notify)
        {
            bool wasEnabled = enabled;
            enabled = false;
            unchecked { recognitionGeneration++; }

            CancellationTokenSource cancellation = recognitionCancellation;
            recognitionCancellation = null;
            if (cancellation != null)
            {
                try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
            }

            IntPtr installedHook = hookHandle;
            hookHandle = IntPtr.Zero;
            if (installedHook != IntPtr.Zero) UnhookWindowsHookEx(installedHook);

            TranslationCursorBadge installedBadge = cursorBadge;
            cursorBadge = null;
            if (installedBadge != null) installedBadge.Dispose();

            // Do not lose an ordinary right click if the user pauses or exits while the
            // double-click window is still open.
            ResetRightGesture(wasEnabled);
            AiSelectionOverlay installedOverlay = selectionOverlay;
            selectionOverlay = null;
            if (installedOverlay != null) installedOverlay.Dispose();
            if (notify && wasEnabled) RaiseEnabledChanged();
        }

        private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0) return CallNextHookEx(hookHandle, code, wParam, lParam);

            int message = wParam.ToInt32();
            if (!enabled || disposed)
            {
                return CallNextHookEx(hookHandle, code, wParam, lParam);
            }

            if (message == WmLButtonDown)
            {
                // Observe only — a left click always reaches the app underneath, and the
                // host dismisses the bubble when the click lands outside it.
                MouseLowLevelHookData leftData = (MouseLowLevelHookData)Marshal.PtrToStructure(
                    lParam, typeof(MouseLowLevelHookData));
                Point leftPoint = new Point(leftData.point.x, leftData.point.y);
                QueueOwner(delegate { RaiseLeftPressed(leftPoint); });
                return CallNextHookEx(hookHandle, code, wParam, lParam);
            }

            if (message != WmMouseMove && message != WmRButtonDown && message != WmRButtonUp)
            {
                return CallNextHookEx(hookHandle, code, wParam, lParam);
            }

            MouseLowLevelHookData data = (MouseLowLevelHookData)Marshal.PtrToStructure(
                lParam, typeof(MouseLowLevelHookData));
            // A delayed native single-right-click is replayed with this private marker. It must
            // pass straight through or the hook would recursively interpret its own click.
            if (data.extraInfo == ReplayedRightClickMarker)
            {
                return CallNextHookEx(hookHandle, code, wParam, lParam);
            }

            Point point = new Point(data.point.x, data.point.y);
            if (message == WmMouseMove)
            {
                HandleRightGestureMove(point);
                return CallNextHookEx(hookHandle, code, wParam, lParam);
            }

            if (message == WmRButtonDown &&
                (HasKeyboardModifier() || IsPointIgnored(point)))
            {
                // A modified click and Windows/app chrome retain their native behaviour. If a
                // first click was waiting to see whether a double-click follows, replay it now.
                ResetRightGesture(true);
                return CallNextHookEx(hookHandle, code, wParam, lParam);
            }

            bool swallow = message == WmRButtonDown
                ? HandleRightButtonDown(point, data.time)
                : HandleRightButtonUp(point, data.time);
            return swallow
                ? new IntPtr(1)
                : CallNextHookEx(hookHandle, code, wParam, lParam);
        }

        private bool HandleRightButtonDown(Point point, uint eventTime)
        {
            bool replayPrior = false;
            lock (gestureLock)
            {
                if (rightGestureState == RightGestureState.WaitingForSecondDown)
                {
                    if (IsDoubleClickCandidate(point, eventTime))
                    {
                        CancelGestureTimersLocked();
                        rightGestureState = RightGestureState.SecondDown;
                        latestRightPoint = point;
                        SetBadgeState(CursorBadgeState.AwaitSecondClick);
                        return true;
                    }

                    replayPrior = true;
                    ClearGestureLocked();
                }

                if (rightGestureState == RightGestureState.Idle)
                {
                    BeginFirstRightDownLocked(point, eventTime);
                    SetBadgeState(CursorBadgeState.AwaitSecondClick);
                }
            }

            // Kept outside the lock because SendInput re-enters this hook synchronously.
            if (replayPrior) ReplaySingleRightClick();
            return true;
        }

        private bool HandleRightButtonUp(Point point, uint eventTime)
        {
            bool replaySingle = false;
            bool recognizePoint = false;
            bool recognizeSelection = false;
            Point selectionStart = Point.Empty;
            Point selectionEnd = Point.Empty;

            lock (gestureLock)
            {
                if (rightGestureState == RightGestureState.FirstDown)
                {
                    latestRightPoint = point;
                    uint heldMilliseconds = unchecked(eventTime - firstRightDownTime);
                    bool draggedFarEnough = HasExceededSystemDragThreshold(firstRightPoint, point);
                    if (aiLongSentenceEnabled && heldMilliseconds >= LongPressMilliseconds && draggedFarEnough)
                    {
                        selectionStart = firstRightPoint;
                        selectionEnd = point;
                        recognizeSelection = true;
                        ClearGestureLocked();
                    }
                    else if (heldMilliseconds >= LongPressMilliseconds)
                    {
                        // A stationary hold is still an ordinary right click.
                        replaySingle = true;
                        ClearGestureLocked();
                    }
                    else
                    {
                        firstRightUpTime = eventTime;
                        rightGestureState = RightGestureState.WaitingForSecondDown;
                        DisposeLongPressTimerLocked();
                        StartSingleRightTimerLocked();
                    }
                }
                else if (rightGestureState == RightGestureState.SecondDown)
                {
                    recognizePoint = true;
                    ClearGestureLocked();
                }
                else if (rightGestureState == RightGestureState.LongDragging)
                {
                    selectionStart = firstRightPoint;
                    selectionEnd = point;
                    recognizeSelection = true;
                    ClearGestureLocked();
                }
                else
                {
                    return false;
                }
            }

            if (replaySingle)
            {
                SetBadgeState(CursorBadgeState.Ready);
                ReplaySingleRightClick();
            }
            if (recognizePoint)
            {
                SetBadgeState(CursorBadgeState.Processing);
                QueueOwner(delegate { StartRecognition(point); });
            }
            if (recognizeSelection)
            {
                SetBadgeState(CursorBadgeState.Processing);
                Point start = selectionStart;
                Point end = selectionEnd;
                QueueOwner(delegate { FinishSelectionGesture(start, end); });
            }
            return true;
        }

        private void HandleRightGestureMove(Point point)
        {
            bool replaySingle = false;
            bool startSelection = false;
            bool updateSelection = false;
            Point start = Point.Empty;

            lock (gestureLock)
            {
                if (rightGestureState == RightGestureState.WaitingForSecondDown &&
                    !IsWithinDoubleClickDistance(firstRightPoint, point))
                {
                    replaySingle = true;
                    ClearGestureLocked();
                }
                else if (rightGestureState == RightGestureState.FirstDown)
                {
                    latestRightPoint = point;
                    if (longPressArmed && aiLongSentenceEnabled &&
                        HasExceededSystemDragThreshold(firstRightPoint, point))
                    {
                        rightGestureState = RightGestureState.LongDragging;
                        start = firstRightPoint;
                        startSelection = true;
                    }
                }
                else if (rightGestureState == RightGestureState.LongDragging)
                {
                    latestRightPoint = point;
                    start = firstRightPoint;
                    updateSelection = true;
                }
            }

            if (replaySingle)
            {
                SetBadgeState(CursorBadgeState.Ready);
                ReplaySingleRightClick();
            }
            if (startSelection)
            {
                Point current = point;
                QueueOwner(delegate { ShowSelectionOverlay(start, current); });
            }
            else if (updateSelection)
            {
                Point current = point;
                QueueOwner(delegate { UpdateSelectionOverlay(start, current); });
            }
        }

        private void BeginFirstRightDownLocked(Point point, uint eventTime)
        {
            unchecked { gestureGeneration++; }
            firstRightPoint = point;
            latestRightPoint = point;
            firstRightDownTime = eventTime;
            firstRightUpTime = 0;
            firstRightTarget = GetRootWindowAt(point);
            longPressArmed = false;
            rightGestureState = RightGestureState.FirstDown;
            DisposeSingleRightTimerLocked();
            DisposeLongPressTimerLocked();
            if (aiLongSentenceEnabled)
            {
                int generation = gestureGeneration;
                longPressTimer = new System.Threading.Timer(
                    delegate { LongPressTimerElapsed(generation); },
                    null,
                    LongPressMilliseconds,
                    Timeout.Infinite);
            }
        }

        private void StartSingleRightTimerLocked()
        {
            DisposeSingleRightTimerLocked();
            int generation = gestureGeneration;
            singleRightTimer = new System.Threading.Timer(
                delegate { SingleRightTimerElapsed(generation); },
                null,
                GetDoubleClickWindowMilliseconds(),
                Timeout.Infinite);
        }

        private void SingleRightTimerElapsed(int generation)
        {
            bool replay = false;
            lock (gestureLock)
            {
                if (!disposed && enabled && generation == gestureGeneration &&
                    rightGestureState == RightGestureState.WaitingForSecondDown)
                {
                    replay = true;
                    ClearGestureLocked();
                }
            }
            if (replay)
            {
                SetBadgeState(CursorBadgeState.Ready);
                ReplaySingleRightClick();
            }
        }

        private void LongPressTimerElapsed(int generation)
        {
            bool startSelection = false;
            Point start = Point.Empty;
            Point current = Point.Empty;
            lock (gestureLock)
            {
                if (disposed || !enabled || !aiLongSentenceEnabled ||
                    generation != gestureGeneration ||
                    rightGestureState != RightGestureState.FirstDown) return;
                longPressArmed = true;
                start = firstRightPoint;
                current = latestRightPoint;
                if (HasExceededSystemDragThreshold(start, current))
                {
                    rightGestureState = RightGestureState.LongDragging;
                    startSelection = true;
                }
            }
            SetBadgeState(CursorBadgeState.SelectingAi);
            if (startSelection)
                QueueOwner(delegate { ShowSelectionOverlay(start, current); });
        }

        private bool IsDoubleClickCandidate(Point point, uint eventTime)
        {
            uint elapsed = unchecked(eventTime - firstRightUpTime);
            if (elapsed > (uint)GetDoubleClickWindowMilliseconds()) return false;
            if (!IsWithinDoubleClickDistance(firstRightPoint, point)) return false;
            IntPtr currentTarget = GetRootWindowAt(point);
            return firstRightTarget == IntPtr.Zero || currentTarget == IntPtr.Zero ||
                   currentTarget == firstRightTarget;
        }

        internal static bool IsWithinDoubleClickDistance(Point first, Point second)
        {
            int width = Math.Max(4, GetSystemMetrics(36));  // SM_CXDOUBLECLK
            int height = Math.Max(4, GetSystemMetrics(37)); // SM_CYDOUBLECLK
            return Math.Abs(first.X - second.X) <= width / 2 &&
                   Math.Abs(first.Y - second.Y) <= height / 2;
        }

        internal static bool HasExceededSystemDragThreshold(Point first, Point second)
        {
            int width = Math.Max(4, GetSystemMetrics(68));  // SM_CXDRAG
            int height = Math.Max(4, GetSystemMetrics(69)); // SM_CYDRAG
            return Math.Abs(first.X - second.X) > width / 2 ||
                   Math.Abs(first.Y - second.Y) > height / 2;
        }

        private static int GetDoubleClickWindowMilliseconds()
        {
            return Math.Max(250, Math.Min(650, unchecked((int)GetDoubleClickTime())));
        }

        private static IntPtr GetRootWindowAt(Point point)
        {
            NativePoint nativePoint = new NativePoint();
            nativePoint.x = point.X;
            nativePoint.y = point.Y;
            IntPtr window = WindowFromPoint(nativePoint);
            if (window == IntPtr.Zero) return IntPtr.Zero;
            IntPtr root = GetAncestor(window, GaRoot);
            return root == IntPtr.Zero ? window : root;
        }

        private void ReplaySingleRightClick()
        {
            if (disposed) return;
            NativeInput[] input = new NativeInput[2];
            input[0].type = 0;
            input[0].mouse.flags = 0x0008; // MOUSEEVENTF_RIGHTDOWN
            input[0].mouse.extraInfo = ReplayedRightClickMarker;
            input[1].type = 0;
            input[1].mouse.flags = 0x0010; // MOUSEEVENTF_RIGHTUP
            input[1].mouse.extraInfo = ReplayedRightClickMarker;
            SendInput((uint)input.Length, input, Marshal.SizeOf(typeof(NativeInput)));
        }

        private void SetBadgeState(CursorBadgeState state)
        {
            QueueOwner(delegate
            {
                TranslationCursorBadge badge = cursorBadge;
                if (badge != null && !badge.IsDisposed) badge.SetState(state);
            });
        }

        private void ResetRightGesture(bool replayPendingSingle)
        {
            bool replay = false;
            lock (gestureLock)
            {
                replay = replayPendingSingle &&
                    (rightGestureState == RightGestureState.FirstDown ||
                     rightGestureState == RightGestureState.WaitingForSecondDown);
                ClearGestureLocked();
            }
            if (selectionOverlay != null && !selectionOverlay.IsDisposed) selectionOverlay.Hide();
            if (replay) ReplaySingleRightClick();
        }

        private void ClearGestureLocked()
        {
            unchecked { gestureGeneration++; }
            rightGestureState = RightGestureState.Idle;
            firstRightPoint = Point.Empty;
            latestRightPoint = Point.Empty;
            firstRightTarget = IntPtr.Zero;
            longPressArmed = false;
            CancelGestureTimersLocked();
        }

        private void CancelGestureTimersLocked()
        {
            DisposeSingleRightTimerLocked();
            DisposeLongPressTimerLocked();
        }

        private void DisposeSingleRightTimerLocked()
        {
            System.Threading.Timer timer = singleRightTimer;
            singleRightTimer = null;
            if (timer != null) timer.Dispose();
        }

        private void DisposeLongPressTimerLocked()
        {
            System.Threading.Timer timer = longPressTimer;
            longPressTimer = null;
            if (timer != null) timer.Dispose();
        }

        private bool IsPointIgnored(Point point)
        {
            NativePoint nativePoint = new NativePoint();
            nativePoint.x = point.X;
            nativePoint.y = point.Y;
            IntPtr window = WindowFromPoint(nativePoint);
            if (window == IntPtr.Zero) return false;
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            if (processId == ownProcessId) return true;

            // Keep the Windows taskbar and notification area usable so the tray menu remains
            // an always-available escape hatch even while right-click translation is armed.
            IntPtr root = GetAncestor(window, GaRoot);
            if (root == IntPtr.Zero) root = window;
            StringBuilder className = new StringBuilder(128);
            GetClassName(root, className, className.Capacity);
            string value = className.ToString();
            return String.Equals(value, "Shell_TrayWnd", StringComparison.Ordinal) ||
                   String.Equals(value, "Shell_SecondaryTrayWnd", StringComparison.Ordinal) ||
                   String.Equals(value, "NotifyIconOverflowWindow", StringComparison.Ordinal);
        }

        private static bool HasKeyboardModifier()
        {
            return IsKeyDown(VkShift) || IsKeyDown(VkControl) || IsKeyDown(VkMenu) ||
                   IsKeyDown(VkLWin) || IsKeyDown(VkRWin);
        }

        private static bool IsKeyDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        private void QueueOwner(MethodInvoker action)
        {
            if (action == null || disposed || owner.IsDisposed || owner.Disposing) return;
            try { owner.BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        private async void StartRecognition(Point point)
        {
            if (disposed || !enabled) return;

            unchecked { recognitionGeneration++; }
            int generation = recognitionGeneration;
            CancellationTokenSource oldCancellation = recognitionCancellation;
            CancellationTokenSource localCancellation = new CancellationTokenSource();
            recognitionCancellation = localCancellation;
            if (oldCancellation != null)
            {
                try { oldCancellation.Cancel(); } catch (ObjectDisposedException) { }
            }

            RaiseProcessing(point);
            TranslationCursorBadge badge = cursorBadge;
            if (badge != null) badge.SuspendForCapture();
            try
            {
                // Let a previous result card disappear from the compositor before capture.
                await Task.Delay(45, localCancellation.Token);
                CaptureFrame frame = await Task.Run(
                    delegate { return CaptureAround(point, localCancellation.Token); },
                    localCancellation.Token);
                localCancellation.Token.ThrowIfCancellationRequested();

                OcrHit hit = await RecognizeNearestWordAsync(
                    ocrEngine, frame, point, localCancellation.Token);
                if (disposed || !enabled || generation != recognitionGeneration ||
                    localCancellation.IsCancellationRequested) return;

                if (hit == null)
                {
                    RaiseFailed(
                        "点击附近没有识别到英文单词。请对准文字中央再试。 / No English word was found near the click.",
                        point);
                    return;
                }
                RaiseRecognized(hit);
            }
            catch (OperationCanceledException)
            {
                // A newer click, pause, or disposal owns the UI now.
            }
            catch (Exception ex)
            {
                if (!disposed && enabled && generation == recognitionGeneration)
                {
                    RaiseFailed(
                        "Windows 本地 OCR 读取失败。 / Windows offline OCR failed. " + FriendlyExceptionMessage(ex),
                        point);
                }
            }
            finally
            {
                if (badge != null && !badge.IsDisposed)
                {
                    badge.SetState(CursorBadgeState.Ready);
                    badge.ResumeAfterCapture();
                }
                if (Object.ReferenceEquals(recognitionCancellation, localCancellation))
                    recognitionCancellation = null;
                localCancellation.Dispose();
            }
        }

        private void ShowSelectionOverlay(Point start, Point current)
        {
            if (disposed || !enabled || !aiLongSentenceEnabled) return;
            if (selectionOverlay == null || selectionOverlay.IsDisposed)
                selectionOverlay = new AiSelectionOverlay();
            selectionOverlay.ShowSelection(start, current);
            SetBadgeState(CursorBadgeState.SelectingAi);
        }

        private void UpdateSelectionOverlay(Point start, Point current)
        {
            if (selectionOverlay == null || selectionOverlay.IsDisposed)
            {
                ShowSelectionOverlay(start, current);
                return;
            }
            selectionOverlay.ShowSelection(start, current);
        }

        private void FinishSelectionGesture(Point start, Point end)
        {
            if (selectionOverlay != null && !selectionOverlay.IsDisposed) selectionOverlay.Hide();
            if (disposed || !enabled || !aiLongSentenceEnabled)
            {
                SetBadgeState(CursorBadgeState.Ready);
                return;
            }

            Rectangle monitor = Screen.FromPoint(start).Bounds;
            Rectangle selection = ComputeSelectionCaptureRegion(start, end, monitor);
            if (selection.IsEmpty)
            {
                SetBadgeState(CursorBadgeState.Ready);
                RaiseFailed(
                    "选区太小。请按住右键后横向拖过完整英文句子。 / Drag across a complete English sentence.",
                    end);
                return;
            }
            StartSelectionRecognition(selection, end);
        }

        private async void StartSelectionRecognition(Rectangle selection, Point anchor)
        {
            if (disposed || !enabled || !aiLongSentenceEnabled) return;

            unchecked { recognitionGeneration++; }
            int generation = recognitionGeneration;
            CancellationTokenSource oldCancellation = recognitionCancellation;
            CancellationTokenSource localCancellation = new CancellationTokenSource();
            recognitionCancellation = localCancellation;
            if (oldCancellation != null)
            {
                try { oldCancellation.Cancel(); } catch (ObjectDisposedException) { }
            }

            RaiseProcessing(anchor);
            TranslationCursorBadge badge = cursorBadge;
            if (badge != null) badge.SuspendForCapture();
            try
            {
                // The selection overlay and previous result bubble must leave the compositor
                // before the exact screen rectangle is copied.
                await Task.Delay(55, localCancellation.Token);
                CaptureFrame frame = await Task.Run(
                    delegate { return CaptureRegion(selection, localCancellation.Token); },
                    localCancellation.Token);
                string text = await RecognizeSelectionTextAsync(
                    ocrEngine, frame, localCancellation.Token);
                if (disposed || !enabled || generation != recognitionGeneration ||
                    localCancellation.IsCancellationRequested) return;

                if (String.IsNullOrWhiteSpace(text))
                {
                    RaiseFailed(
                        "选区内没有识别到清晰英文。请放大文字后重试。 / No clear English was found in the selection.",
                        anchor);
                    return;
                }
                if (text.Length > 3000)
                {
                    RaiseFailed(
                        "所选英文超过 3,000 个字符，请缩小选区。 / The selected English is longer than 3,000 characters.",
                        anchor);
                    return;
                }
                RaiseSelectionRecognized(text, selection, anchor);
            }
            catch (OperationCanceledException)
            {
                // A newer gesture, pause, or shutdown owns the UI.
            }
            catch (Exception ex)
            {
                if (!disposed && enabled && generation == recognitionGeneration)
                {
                    RaiseFailed(
                        "Windows 本地 OCR 无法读取所选长句。 / Windows OCR could not read the selected sentence. " +
                        FriendlyExceptionMessage(ex),
                        anchor);
                }
            }
            finally
            {
                if (badge != null && !badge.IsDisposed)
                {
                    badge.SetState(CursorBadgeState.Ready);
                    badge.ResumeAfterCapture();
                }
                if (Object.ReferenceEquals(recognitionCancellation, localCancellation))
                    recognitionCancellation = null;
                localCancellation.Dispose();
            }
        }

        private static CaptureFrame CaptureAround(Point point, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Keep one capture on one monitor; a virtual-screen rectangle can straddle monitors
            // with different DPI/scaling and distort the OCR word coordinates.
            Rectangle captureScreen = Screen.FromPoint(point).Bounds;
            Rectangle region = ComputeCaptureRegion(point, captureScreen);
            return CaptureRegion(region, cancellationToken);
        }

        private static CaptureFrame CaptureRegion(Rectangle region, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int width = region.Width;
            int height = region.Height;
            if (width <= 0 || height <= 0) throw new InvalidOperationException("No screen capture area is available.");

            using (Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(
                        region.Location,
                        Point.Empty,
                        region.Size,
                        CopyPixelOperation.SourceCopy);
                }
                cancellationToken.ThrowIfCancellationRequested();
                using (MemoryStream encoded = new MemoryStream())
                {
                    bitmap.Save(encoded, ImageFormat.Png);
                    return new CaptureFrame(region, encoded.ToArray());
                }
            }
        }

        internal static Rectangle ComputeSelectionCaptureRegion(
            Point start,
            Point end,
            Rectangle monitor)
        {
            int left = Math.Min(start.X, end.X);
            int top = Math.Min(start.Y, end.Y);
            int right = Math.Max(start.X, end.X);
            int bottom = Math.Max(start.Y, end.Y);
            int rawWidth = right - left;
            int rawHeight = bottom - top;
            // A horizontal sentence drag may be deliberately narrow vertically, but it must
            // still be long enough to distinguish it from hand jitter.
            if (rawWidth < 36 || rawHeight < 6 || monitor.Width <= 0 || monitor.Height <= 0)
                return Rectangle.Empty;

            Rectangle expanded = Rectangle.FromLTRB(
                left - 10,
                top - 18,
                right + 11,
                bottom + 19);
            Rectangle clipped = Rectangle.Intersect(expanded, monitor);
            return clipped.Width >= 36 && clipped.Height >= 18
                ? clipped
                : Rectangle.Empty;
        }

        internal static Rectangle ComputeCaptureRegion(Point point, Rectangle captureScreen)
        {
            int width = Math.Min(1000, captureScreen.Width);
            int height = Math.Min(320, captureScreen.Height);
            if (width <= 0 || height <= 0) return Rectangle.Empty;

            int x = point.X - (width / 2);
            int y = point.Y - (height / 2);
            x = Math.Max(captureScreen.Left, Math.Min(x, captureScreen.Right - width));
            y = Math.Max(captureScreen.Top, Math.Min(y, captureScreen.Bottom - height));
            return new Rectangle(x, y, width, height);
        }

        private static async Task<OcrHit> RecognizeNearestWordAsync(
            OcrEngine engine,
            CaptureFrame frame,
            Point originalClick,
            CancellationToken cancellationToken)
        {
            if (engine == null) throw new InvalidOperationException("The Windows OCR engine is unavailable.");
            using (InMemoryRandomAccessStream randomAccess = new InMemoryRandomAccessStream())
            {
                using (DataWriter writer = new DataWriter(randomAccess.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(frame.PngBytes);
                    await ToTask(writer.StoreAsync(), cancellationToken);
                    await ToTask(writer.FlushAsync(), cancellationToken);
                    writer.DetachStream();
                }
                cancellationToken.ThrowIfCancellationRequested();
                randomAccess.Seek(0);

                BitmapDecoder decoder = await ToTask(BitmapDecoder.CreateAsync(randomAccess), cancellationToken);
                using (SoftwareBitmap softwareBitmap = await ToTask(
                    decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied),
                    cancellationToken))
                {
                    OcrResult result = await ToTask(engine.RecognizeAsync(softwareBitmap), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    return SelectNearestEnglishWord(result, frame.Region, originalClick);
                }
            }
        }

        private static async Task<string> RecognizeSelectionTextAsync(
            OcrEngine engine,
            CaptureFrame frame,
            CancellationToken cancellationToken)
        {
            if (engine == null) throw new InvalidOperationException("The Windows OCR engine is unavailable.");
            using (InMemoryRandomAccessStream randomAccess = new InMemoryRandomAccessStream())
            {
                using (DataWriter writer = new DataWriter(randomAccess.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(frame.PngBytes);
                    await ToTask(writer.StoreAsync(), cancellationToken);
                    await ToTask(writer.FlushAsync(), cancellationToken);
                    writer.DetachStream();
                }
                cancellationToken.ThrowIfCancellationRequested();
                randomAccess.Seek(0);

                BitmapDecoder decoder = await ToTask(BitmapDecoder.CreateAsync(randomAccess), cancellationToken);
                using (SoftwareBitmap softwareBitmap = await ToTask(
                    decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied),
                    cancellationToken))
                {
                    OcrResult result = await ToTask(engine.RecognizeAsync(softwareBitmap), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    return ExtractEnglishSelectionText(result);
                }
            }
        }

        private static string ExtractEnglishSelectionText(OcrResult result)
        {
            if (result == null || result.Lines == null) return String.Empty;
            List<string> lines = new List<string>();
            foreach (OcrLine line in result.Lines)
            {
                if (line == null) continue;
                string value = line.Text;
                if (String.IsNullOrWhiteSpace(value) && line.Words != null)
                {
                    List<string> words = new List<string>();
                    foreach (OcrWord word in line.Words)
                    {
                        if (word != null && !String.IsNullOrWhiteSpace(word.Text))
                            words.Add(word.Text.Trim());
                    }
                    value = String.Join(" ", words.ToArray());
                }
                value = Regex.Replace(value == null ? String.Empty : value.Trim(), @"\s+", " ");
                if (value.Length > 0) lines.Add(value);
            }
            return NormalizeSelectionText(String.Join(" ", lines.ToArray()));
        }

        internal static string NormalizeSelectionText(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return String.Empty;
            string value = text.Replace("\0", String.Empty).Replace("\u00AD", String.Empty);
            value = Regex.Replace(value, @"\s+", " ").Trim();
            int latinLetters = 0;
            foreach (char character in value)
            {
                if ((character >= 'A' && character <= 'Z') ||
                    (character >= 'a' && character <= 'z') ||
                    (character >= '\u00C0' && character <= '\u024F'))
                {
                    latinLetters++;
                }
                else if (Char.IsLetter(character))
                {
                    // The AI endpoints are deliberately English-only. Mixed-script OCR is
                    // ambiguous and must not silently send unrelated on-screen text.
                    return String.Empty;
                }
            }
            return latinLetters >= 2 ? value : String.Empty;
        }

        private static OcrHit SelectNearestEnglishWord(OcrResult result, Rectangle region, Point click)
        {
            if (result == null || result.Lines == null) return null;
            double localX = click.X - region.Left;
            double localY = click.Y - region.Top;
            WordCandidate nearest = null;

            foreach (OcrLine line in result.Lines)
            {
                if (line == null || line.Words == null) continue;
                List<string> lineWords = new List<string>();
                foreach (OcrWord item in line.Words)
                {
                    lineWords.Add(item == null ? String.Empty : (item.Text ?? String.Empty).Trim());
                }

                for (int index = 0; index < line.Words.Count; index++)
                {
                    OcrWord word = line.Words[index];
                    if (word == null) continue;
                    string normalized = NormalizeEnglishWord(word.Text);
                    if (String.IsNullOrEmpty(normalized)) continue;
                    double rectangleX;
                    double rectangleY;
                    double rectangleWidth;
                    double rectangleHeight;
                    if (!TryReadBoundingRect(
                        word,
                        out rectangleX,
                        out rectangleY,
                        out rectangleWidth,
                        out rectangleHeight)) continue;
                    double distance = DistanceToRectangle(
                        localX,
                        localY,
                        rectangleX,
                        rectangleY,
                        rectangleWidth,
                        rectangleHeight);
                    if (nearest == null || distance < nearest.Distance)
                    {
                        string lineText = String.IsNullOrWhiteSpace(line.Text)
                            ? String.Join(" ", lineWords.ToArray()).Trim()
                            : line.Text.Trim();
                        nearest = new WordCandidate(
                            normalized, lineText, lineWords, index, distance);
                    }
                }
            }

            // Avoid surprising translations of unrelated text at the far side of the capture.
            if (nearest == null || nearest.Distance > 150.0) return null;
            return new OcrHit(
                nearest.Word,
                nearest.LineText,
                nearest.LineWords,
                nearest.WordIndex,
                click);
        }

        internal static string NormalizeEnglishWord(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return String.Empty;
            string value = text.Trim();
            value = Regex.Replace(value, @"^[^A-Za-zÀ-ÖØ-öø-ÿ]+", String.Empty);
            value = Regex.Replace(value, @"[^A-Za-zÀ-ÖØ-öø-ÿ]+$", String.Empty);
            return Regex.IsMatch(
                value,
                @"^[A-Za-zÀ-ÖØ-öø-ÿ]+(?:['’\-][A-Za-zÀ-ÖØ-öø-ÿ]+)*$")
                ? value
                : String.Empty;
        }

        // BoundingRect is projected through System.Runtime.WindowsRuntime on some .NET Framework
        // installations. Reading its public value members by reflection keeps this assembly free of
        // that reference while still selecting the word from OcrWord.BoundingRect geometry.
        private static bool TryReadBoundingRect(
            OcrWord word,
            out double x,
            out double y,
            out double width,
            out double height)
        {
            x = 0.0;
            y = 0.0;
            width = 0.0;
            height = 0.0;
            try
            {
                PropertyInfo property = word.GetType().GetProperty(
                    "BoundingRect", BindingFlags.Instance | BindingFlags.Public);
                if (property == null) return false;
                object rectangle = property.GetValue(word, null);
                if (rectangle == null) return false;
                Type rectangleType = rectangle.GetType();
                x = ReadDoubleMember(rectangle, rectangleType, "X");
                y = ReadDoubleMember(rectangle, rectangleType, "Y");
                width = ReadDoubleMember(rectangle, rectangleType, "Width");
                height = ReadDoubleMember(rectangle, rectangleType, "Height");
                return width > 0.0 && height > 0.0;
            }
            catch
            {
                return false;
            }
        }

        private static double ReadDoubleMember(object instance, Type type, string name)
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null) return Convert.ToDouble(property.GetValue(instance, null));
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            if (field != null) return Convert.ToDouble(field.GetValue(instance));
            throw new MissingMemberException(type.FullName, name);
        }

        private static double DistanceToRectangle(
            double x, double y, double rectangleX, double rectangleY,
            double rectangleWidth, double rectangleHeight)
        {
            double dx = 0.0;
            double dy = 0.0;
            if (x < rectangleX) dx = rectangleX - x;
            else if (x > rectangleX + rectangleWidth) dx = x - (rectangleX + rectangleWidth);
            if (y < rectangleY) dy = rectangleY - y;
            else if (y > rectangleY + rectangleHeight) dy = y - (rectangleY + rectangleHeight);
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private bool TryCreateEnglishOcrEngine(out OcrEngine engine, out string tag, out string error)
        {
            engine = null;
            tag = String.Empty;
            error = String.Empty;
            try
            {
                Language[] installed = OcrEngine.AvailableRecognizerLanguages.ToArray();
                List<string> preferences = new List<string>();
                if (!String.IsNullOrWhiteSpace(requestedLanguageTag)) preferences.Add(requestedLanguageTag);
                preferences.Add("en-SG");
                preferences.Add("en-GB");
                preferences.Add("en-US");

                Language selected = null;
                foreach (string preference in preferences)
                {
                    selected = installed.FirstOrDefault(delegate(Language candidate)
                    {
                        return String.Equals(candidate.LanguageTag, preference, StringComparison.OrdinalIgnoreCase);
                    });
                    if (selected != null) break;
                }
                if (selected == null)
                {
                    selected = installed.FirstOrDefault(delegate(Language candidate)
                    {
                        return candidate.LanguageTag.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ||
                               String.Equals(candidate.LanguageTag, "en", StringComparison.OrdinalIgnoreCase);
                    });
                }
                if (selected == null)
                {
                    error = "Windows 没有安装英语 OCR 语言包。请在系统语言设置中添加英语的基本输入/OCR功能。 / No English Windows OCR language is installed.";
                    return false;
                }

                engine = OcrEngine.TryCreateFromLanguage(selected);
                if (engine == null)
                {
                    error = "无法启动 Windows 英语 OCR（" + selected.LanguageTag + "）。 / Windows English OCR could not start.";
                    return false;
                }
                tag = engine.RecognizerLanguage == null
                    ? selected.LanguageTag
                    : engine.RecognizerLanguage.LanguageTag;
                return true;
            }
            catch (Exception ex)
            {
                error = "此 Windows 系统无法使用本地 OCR。 / Windows offline OCR is unavailable. " +
                        FriendlyExceptionMessage(ex);
                return false;
            }
        }

        // Deliberately mirrors the probe bridge. It does not reference System.Runtime.WindowsRuntime.
        private static Task<T> ToTask<T>(WinFoundation.IAsyncOperation<T> operation, CancellationToken cancellationToken)
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
                    {
                        completion.TrySetResult(sender.GetResults());
                    }
                    else if (status == WinFoundation.AsyncStatus.Canceled)
                    {
                        completion.TrySetCanceled();
                    }
                    else
                    {
                        completion.TrySetException(
                            sender.ErrorCode ?? new InvalidOperationException("WinRT operation failed."));
                    }
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

        private static string FriendlyExceptionMessage(Exception ex)
        {
            if (ex == null || String.IsNullOrWhiteSpace(ex.Message)) return "Unknown error.";
            return ex.Message.Trim();
        }

        private void RaiseRecognized(OcrHit hit)
        {
            EventHandler<OcrHitEventArgs> handler = Recognized;
            if (handler != null)
            {
                try { handler(this, new OcrHitEventArgs(hit)); } catch { }
            }
        }

        private void RaiseSelectionRecognized(string text, Rectangle bounds, Point point)
        {
            EventHandler<OcrSelectionEventArgs> handler = SelectionRecognized;
            if (handler != null)
            {
                try { handler(this, new OcrSelectionEventArgs(text, bounds, point)); } catch { }
            }
        }

        private void RaiseFailed(string message, Point point)
        {
            EventHandler<OcrFailureEventArgs> handler = Failed;
            if (handler != null)
            {
                try { handler(this, new OcrFailureEventArgs(message, point)); } catch { }
            }
        }

        private void RaiseProcessing(Point point)
        {
            EventHandler<OcrPointEventArgs> handler = Processing;
            if (handler != null)
            {
                try { handler(this, new OcrPointEventArgs(point)); } catch { }
            }
        }

        private void RaiseLeftPressed(Point point)
        {
            EventHandler<OcrPointEventArgs> handler = LeftPressed;
            if (handler != null)
            {
                try { handler(this, new OcrPointEventArgs(point)); } catch { }
            }
        }

        private void RaiseEnabledChanged()
        {
            EventHandler handler = EnabledChanged;
            if (handler != null)
            {
                try { handler(this, EventArgs.Empty); } catch { }
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException("TranslationMouseController");
        }

        public void Dispose()
        {
            if (disposed) return;
            DisableCore(false);
            disposed = true;
            GC.SuppressFinalize(this);
        }

        private sealed class CaptureFrame
        {
            internal Rectangle Region;
            internal byte[] PngBytes;

            internal CaptureFrame(Rectangle region, byte[] pngBytes)
            {
                Region = region;
                PngBytes = pngBytes;
            }
        }

        private sealed class WordCandidate
        {
            internal string Word;
            internal string LineText;
            internal IList<string> LineWords;
            internal int WordIndex;
            internal double Distance;

            internal WordCandidate(
                string word,
                string lineText,
                IList<string> lineWords,
                int wordIndex,
                double distance)
            {
                Word = word;
                LineText = lineText;
                LineWords = lineWords;
                WordIndex = wordIndex;
                Distance = distance;
            }
        }

        private enum RightGestureState
        {
            Idle,
            FirstDown,
            WaitingForSecondDown,
            SecondDown,
            LongDragging
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            internal int x;
            internal int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseLowLevelHookData
        {
            internal NativePoint point;
            internal uint mouseData;
            internal uint flags;
            internal uint time;
            internal UIntPtr extraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeInput
        {
            internal uint type;
            internal NativeMouseInput mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMouseInput
        {
            internal int dx;
            internal int dy;
            internal uint mouseData;
            internal uint flags;
            internal uint time;
            internal UIntPtr extraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int hookId, LowLevelMouseProc callback, IntPtr instance, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(NativePoint point);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);
    }

    /// <summary>
    /// Presents a 32-bit bitmap with its real per-pixel alpha on a click-through layered form.
    /// This avoids the coloured fringe produced by WinForms TransparencyKey around soft curves.
    /// </summary>
    internal static class LayeredWindowSurface
    {
        private const int UlwAlpha = 0x00000002;
        private const byte AcSrcOver = 0x00;
        private const byte AcSrcAlpha = 0x01;

        internal static void Present(Form window, Bitmap bitmap, Point location)
        {
            if (window == null || bitmap == null || window.IsDisposed || !window.IsHandleCreated) return;
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memoryDc = IntPtr.Zero;
            IntPtr bitmapHandle = IntPtr.Zero;
            IntPtr previous = IntPtr.Zero;
            try
            {
                memoryDc = CreateCompatibleDC(screenDc);
                if (memoryDc == IntPtr.Zero) return;
                bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
                if (bitmapHandle == IntPtr.Zero) return;
                previous = SelectObject(memoryDc, bitmapHandle);

                NativePoint destination = new NativePoint(location.X, location.Y);
                NativeSize size = new NativeSize(bitmap.Width, bitmap.Height);
                NativePoint source = new NativePoint(0, 0);
                BlendFunction blend = new BlendFunction();
                blend.BlendOp = AcSrcOver;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AcSrcAlpha;
                UpdateLayeredWindow(
                    window.Handle,
                    screenDc,
                    ref destination,
                    ref size,
                    memoryDc,
                    ref source,
                    0,
                    ref blend,
                    UlwAlpha);
            }
            finally
            {
                if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero)
                    SelectObject(memoryDc, previous);
                if (bitmapHandle != IntPtr.Zero) DeleteObject(bitmapHandle);
                if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            internal int x;
            internal int y;

            internal NativePoint(int x, int y)
            {
                this.x = x;
                this.y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            internal int width;
            internal int height;

            internal NativeSize(int width, int height)
            {
                this.width = width;
                this.height = height;
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction
        {
            internal byte BlendOp;
            internal byte BlendFlags;
            internal byte SourceConstantAlpha;
            internal byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateLayeredWindow(
            IntPtr window,
            IntPtr destinationDc,
            ref NativePoint destination,
            ref NativeSize size,
            IntPtr sourceDc,
            ref NativePoint source,
            int colourKey,
            ref BlendFunction blend,
            int flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr value);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteDC(IntPtr dc);
    }

    /// <summary>A click-through mint/lavender rectangle shown while selecting an AI sentence.</summary>
    internal sealed class AiSelectionOverlay : Form
    {
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExLayered = 0x00080000;
        private const int WsExNoActivate = 0x08000000;
        private const int WmNcHitTest = 0x0084;
        private const int HtTransparent = -1;

        internal AiSelectionOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate;
                return parameters;
            }
        }

        internal void ShowSelection(Point start, Point current)
        {
            // Match the local OCR capture padding so a mostly horizontal sentence drag still
            // appears as a clear, airy text band instead of a hard-to-see two-pixel line.
            int left = Math.Min(start.X, current.X) - 10;
            int top = Math.Min(start.Y, current.Y) - 18;
            int width = Math.Max(22, Math.Abs(current.X - start.X) + 20);
            int height = Math.Max(38, Math.Abs(current.Y - start.Y) + 36);
            Bounds = new Rectangle(left, top, width, height);
            if (!Visible) Show();
            RenderLayered();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            DrawSelection(e.Graphics);
        }

        private void RenderLayered()
        {
            if (IsDisposed || Width <= 0 || Height <= 0 || !IsHandleCreated) return;
            using (Bitmap bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                DrawSelection(graphics);
                LayeredWindowSurface.Present(this, bitmap, Location);
            }
        }

        private void DrawSelection(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float scale = Math.Max(1F, DeviceDpi / 96F);
            Rectangle bounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));

            using (GraphicsPath band = RoundedGeometry.Create(bounds, (int)Math.Round(9F * scale)))
            {
                using (LinearGradientBrush tint = new LinearGradientBrush(
                    bounds,
                    Color.FromArgb(58, 66, 220, 190),
                    Color.FromArgb(48, 130, 112, 246),
                    LinearGradientMode.ForwardDiagonal))
                    graphics.FillPath(tint, band);
                using (Pen border = new Pen(Color.FromArgb(240, 54, 208, 179), 1.6F * scale))
                    graphics.DrawPath(border, band);
            }

            // A whisper-thin inner hairline keeps the band crisp on busy backgrounds.
            int inset = (int)Math.Round(2F * scale);
            Rectangle inner = Rectangle.Inflate(bounds, -inset, -inset);
            if (inner.Width > 6 && inner.Height > 6)
            {
                using (GraphicsPath innerPath = RoundedGeometry.Create(inner, (int)Math.Round(7F * scale)))
                using (Pen hairline = new Pen(Color.FromArgb(52, 255, 255, 255), 1F * scale))
                    graphics.DrawPath(hairline, innerPath);
            }

            if (Width >= (int)(92F * scale) && Height >= (int)(26F * scale))
            {
                Rectangle chip = new Rectangle(
                    (int)Math.Round(9F * scale),
                    (int)Math.Round(8F * scale),
                    (int)Math.Round(68F * scale),
                    (int)Math.Round(21F * scale));
                using (GraphicsPath path = RoundedGeometry.Create(chip, (int)Math.Round(10F * scale)))
                {
                    using (LinearGradientBrush fill = new LinearGradientBrush(
                        chip,
                        Color.FromArgb(250, 40, 202, 173),
                        Color.FromArgb(247, 120, 102, 234),
                        14F))
                        graphics.FillPath(fill, path);
                    using (Pen rim = new Pen(Color.FromArgb(185, 255, 255, 255), 1F * scale))
                        graphics.DrawPath(rim, path);
                }
                using (Brush text = new SolidBrush(Color.White))
                using (Font font = new Font("Microsoft YaHei UI", 9F * scale, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    StringFormat format = new StringFormat();
                    try
                    {
                        format.Alignment = StringAlignment.Center;
                        format.LineAlignment = StringAlignment.Center;
                        graphics.DrawString("✦ AI 长句", font, text, chip, format);
                    }
                    finally { format.Dispose(); }
                }
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmNcHitTest)
            {
                message.Result = new IntPtr(HtTransparent);
                return;
            }
            base.WndProc(ref message);
        }
    }

    /// <summary>
    /// A light, animated visual marker that follows the untouched Windows pointer. Keeping the
    /// user's real Arrow/I-beam/Hand/Wait cursor preserves its shape and hotspot in every app.
    /// </summary>
    public sealed class TranslationCursorBadge : Form
    {
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExLayered = 0x00080000;
        private const int WsExNoActivate = 0x08000000;
        private const int WmNcHitTest = 0x0084;
        private const int HtTransparent = -1;
        private const int WmDpiChanged = 0x02E0;
        private readonly System.Windows.Forms.Timer followTimer;
        private int captureSuspensions;
        private int animationFrame;
        private CursorBadgeState state;
        private float renderScale = 1F;

        public TranslationCursorBadge()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(46, 30);
            DoubleBuffered = true;
            state = CursorBadgeState.Ready;
            followTimer = new System.Windows.Forms.Timer();
            followTimer.Interval = 30;
            followTimer.Tick += delegate
            {
                // 12480 is a common multiple of every animation period used below,
                // so the wrap never causes a visible phase jump.
                animationFrame = (animationFrame + 1) % 12480;
                FollowPointer();
                RenderLayered();
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateRenderScale();
        }

        private void UpdateRenderScale()
        {
            float scale = Math.Max(1F, DeviceDpi / 96F);
            renderScale = scale;
            ClientSize = new Size(
                (int)Math.Round(46F * scale),
                (int)Math.Round(30F * scale));
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate;
                return parameters;
            }
        }

        public void StartBadge()
        {
            if (IsDisposed) return;
            captureSuspensions = 0;
            state = CursorBadgeState.Ready;
            FollowPointer();
            followTimer.Start();
            if (!Visible) Show();
            RenderLayered();
        }

        internal void SetState(CursorBadgeState value)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<CursorBadgeState>(SetState), value);
                return;
            }
            if (state == value)
            {
                // Re-arming the same state restarts its rhythm without an extra frame.
                animationFrame = 0;
                return;
            }
            state = value;
            animationFrame = 0;
            RenderLayered();
        }

        public void SuspendForCapture()
        {
            if (IsDisposed) return;
            captureSuspensions++;
            if (Visible) Hide();
        }

        public void ResumeAfterCapture()
        {
            if (IsDisposed) return;
            if (captureSuspensions > 0) captureSuspensions--;
            if (captureSuspensions == 0)
            {
                FollowPointer();
                if (!Visible) Show();
                RenderLayered();
            }
        }

        private void FollowPointer()
        {
            // Instant follow. The earlier damped chase read as "cursor drift" while
            // dragging an AI selection, so the badge now tracks the pointer exactly.
            if (IsDisposed || captureSuspensions > 0) return;
            Point pointer = Cursor.Position;
            Location = new Point(
                pointer.X + (int)Math.Round(19F * renderScale),
                pointer.Y + (int)Math.Round(17F * renderScale));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            DrawBadge(e.Graphics);
        }

        private void RenderLayered()
        {
            if (IsDisposed || captureSuspensions > 0 || !Visible || !IsHandleCreated) return;
            using (Bitmap bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                DrawBadge(graphics);
                LayeredWindowSurface.Present(this, bitmap, Location);
            }
        }

        private void DrawBadge(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // All geometry below is authored on a 46×30 canvas; a single transform
            // renders it crisply at any monitor scale.
            if (renderScale > 1F) graphics.ScaleTransform(renderScale, renderScale);

            if (state == CursorBadgeState.SelectingAi)
            {
                DrawAiChip(graphics);
                return;
            }
            if (state == CursorBadgeState.Processing)
            {
                DrawProcessingWave(graphics);
                return;
            }
            DrawLumaOrb(graphics, state == CursorBadgeState.AwaitSecondClick);
        }

        /// <summary>Ready / waiting marker: a breathing mint orb with one orbiting spark.</summary>
        private void DrawLumaOrb(Graphics graphics, bool awaitingSecondClick)
        {
            float breath = (float)((Math.Sin(animationFrame * Math.PI / 26.0) + 1.0) / 2.0);
            PointF centre = new PointF(13F, 15F);
            double angle = animationFrame * Math.PI / 32.0;
            float satX = centre.X + (float)(Math.Cos(angle) * 10.2);
            float satY = centre.Y + (float)(Math.Sin(angle) * 3.4);
            bool satelliteInFront = Math.Sin(angle) >= 0.0;

            if (!awaitingSecondClick && !satelliteInFront)
                DrawSatellite(graphics, satX, satY, false);

            // Soft halo that gently swells with the breath cycle.
            float haloGrow = breath * 1.6F;
            RectangleF halo = new RectangleF(
                centre.X - 10.5F - haloGrow, centre.Y - 10.5F - haloGrow,
                21F + haloGrow * 2F, 21F + haloGrow * 2F);
            using (GraphicsPath haloPath = new GraphicsPath())
            {
                haloPath.AddEllipse(halo);
                using (PathGradientBrush glow = new PathGradientBrush(haloPath))
                {
                    glow.CenterColor = Color.FromArgb(64 + (int)(26 * breath), 96, 228, 200);
                    glow.SurroundColors = new Color[] { Color.FromArgb(0, 96, 228, 200) };
                    graphics.FillPath(glow, haloPath);
                }
            }

            // Core orb: mint → sky gradient sphere with a white rim and specular dot.
            RectangleF core = new RectangleF(centre.X - 6F, centre.Y - 6F, 12F, 12F);
            using (LinearGradientBrush fill = new LinearGradientBrush(
                new Rectangle((int)core.X - 1, (int)core.Y - 1, 14, 14),
                Color.FromArgb(252, 52, 214, 183),
                Color.FromArgb(252, 92, 152, 244),
                LinearGradientMode.ForwardDiagonal))
                graphics.FillEllipse(fill, core);
            using (Pen rim = new Pen(Color.FromArgb(215, 255, 255, 255), 1.1F))
                graphics.DrawEllipse(rim, core);
            using (SolidBrush specular = new SolidBrush(Color.FromArgb(185, 255, 255, 255)))
                graphics.FillEllipse(specular, centre.X - 3.4F, centre.Y - 3.8F, 3.4F, 3.4F);

            if (awaitingSecondClick)
            {
                // Two metronome dots: "click · click" — the second beat answers the first.
                bool firstBeat = (animationFrame / 10) % 2 == 0;
                int firstAlpha = firstBeat ? 240 : 88;
                int secondAlpha = firstBeat ? 88 : 240;
                using (SolidBrush first = new SolidBrush(Color.FromArgb(firstAlpha, 62, 216, 186)))
                    graphics.FillEllipse(first, 27.5F, 11.6F, 6.4F, 6.4F);
                using (SolidBrush second = new SolidBrush(Color.FromArgb(secondAlpha, 140, 116, 242)))
                    graphics.FillEllipse(second, 36.5F, 11.6F, 6.4F, 6.4F);
            }
            else if (satelliteInFront)
            {
                DrawSatellite(graphics, satX, satY, true);
            }
        }

        private static void DrawSatellite(Graphics graphics, float x, float y, bool inFront)
        {
            int alpha = inFront ? 225 : 95;
            float size = inFront ? 4.4F : 3.2F;
            using (SolidBrush spark = new SolidBrush(Color.FromArgb(alpha, 150, 128, 244)))
                graphics.FillEllipse(spark, x - size / 2F, y - size / 2F, size, size);
        }

        /// <summary>OCR/AI progress: a three-dot wave sweeping mint → violet.</summary>
        private void DrawProcessingWave(Graphics graphics)
        {
            for (int index = 0; index < 3; index++)
            {
                double phase = (animationFrame * 0.40) - (index * 0.95);
                float lift = (float)(Math.Sin(phase) * 3.1);
                float blend = index / 2F;
                int red = (int)(56 + (148 - 56) * blend);
                int green = (int)(212 + (126 - 212) * blend);
                int blue = (int)(186 + (242 - 186) * blend);
                int alpha = 150 + (int)(92.0 * ((Math.Sin(phase) + 1.0) / 2.0));
                using (SolidBrush dot = new SolidBrush(Color.FromArgb(alpha, red, green, blue)))
                    graphics.FillEllipse(dot, 7.4F + index * 12.2F, 12.6F - lift, 7.2F, 7.2F);
            }
        }

        /// <summary>Long-press AI selection chip with a softly pulsing rim.</summary>
        private void DrawAiChip(Graphics graphics)
        {
            Rectangle chip = new Rectangle(2, 5, 40, 20);
            float pulse = (float)((Math.Sin(animationFrame * Math.PI / 15.0) + 1.0) / 2.0);
            using (GraphicsPath path = RoundedGeometry.Create(chip, 10))
            {
                using (LinearGradientBrush fill = new LinearGradientBrush(
                    chip,
                    Color.FromArgb(250, 42, 205, 176),
                    Color.FromArgb(248, 122, 103, 236),
                    16F))
                    graphics.FillPath(fill, path);
                using (Pen rim = new Pen(Color.FromArgb(150 + (int)(85 * pulse), 255, 255, 255), 1.1F))
                    graphics.DrawPath(rim, path);
            }
            using (Brush text = new SolidBrush(Color.White))
            using (Font font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                StringFormat format = new StringFormat();
                try
                {
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    graphics.DrawString("AI", font, text, new RectangleF(2, 5, 40, 20), format);
                }
                finally { format.Dispose(); }
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmNcHitTest)
            {
                message.Result = new IntPtr(HtTransparent);
                return;
            }
            if (message.Msg == WmDpiChanged)
            {
                UpdateRenderScale();
            }
            base.WndProc(ref message);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && followTimer != null)
            {
                followTimer.Stop();
                followTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A compact dictionary bubble displayed beside the clicked word.  It deliberately
    /// opens without stealing focus; clicking one of its real Button controls still
    /// activates it, so keyboard navigation and assistive technology continue to work.
    /// </summary>
    public sealed class QuickTranslationPopup : Form
    {
        private const int LogicalWidth = 396;
        private const int LogicalTailHeight = 13;
        private const int WmDpiChanged = 0x02E0;
        private const uint MonitorDefaultToNearest = 2;

        private readonly Label sourceLabel;
        private readonly Label phoneticLabel;
        private readonly PillLabel partOfSpeechPill;
        private readonly PillLabel providerPill;
        private readonly Label translationLabel;
        private readonly Label explanationCaption;
        private readonly Label explanationLabel;
        private readonly Label usageCaption;
        private readonly Label usageLabel;
        private readonly BubbleButton speakButton;
        private readonly BubbleButton explainButton;
        private readonly BubbleButton aiButton;
        private readonly BubbleButton moreButton;
        private readonly BubbleButton pauseButton;
        private readonly BubbleButton closeButton;
        private readonly ToolTip fullTextTip;
        private readonly System.Windows.Forms.Timer appearTimer;
        private int appearStep;
        private int appearBaseTop;
        private Font headlineFont;

        private string currentText;
        private string currentLineText;
        private string currentPartOfSpeech;
        private string currentUsage;
        private string currentProvider;
        private TranslationResult currentResult;
        private Rectangle usageCardBounds;
        private Rectangle dividerBounds;
        private int currentDpi = 96;
        private bool wideLayout;
        private int tailCentre;
        private bool tailOnTop;
        private bool aiBusy;
        private bool hasAiUsage;

        public event EventHandler SpeakRequested;
        public event EventHandler ExplainRequested;
        public event EventHandler MoreRequested;
        public event EventHandler PauseRequested;

        /// <summary>Raised when the user requests an AI-generated practical usage note.</summary>
        public event EventHandler AiRequested;

        /// <summary>Raised after the user dismisses this bubble without pausing point translation.</summary>
        public event EventHandler CloseRequested;

        public string CurrentText { get { return currentText ?? String.Empty; } }
        public string CurrentLineText { get { return currentLineText ?? String.Empty; } }
        public TranslationResult CurrentResult { get { return currentResult; } }
        public string CurrentPartOfSpeech { get { return currentPartOfSpeech ?? String.Empty; } }
        public string CurrentUsage { get { return currentUsage ?? String.Empty; } }
        public string CurrentProvider { get { return currentProvider ?? String.Empty; } }
        public bool HasAiUsage { get { return hasAiUsage; } }

        /// <summary>
        /// True makes the first Show non-activating.  The form intentionally does not use
        /// WS_EX_NOACTIVATE: a user who clicks a button must still be able to tab through it.
        /// </summary>
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                parameters.ExStyle |= 0x00000080;    // WS_EX_TOOLWINDOW
                return parameters;
            }
        }

        public QuickTranslationPopup()
        {
            Text = "即时英译中 / Quick translation";
            Name = "QuickTranslationPopup";
            AccessibleName = "即时英译中词典卡片";
            AccessibleDescription = "显示英文、词性、中文释义、英文解释和生活用法。";
            AccessibleRole = AccessibleRole.Dialog;
            Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point);
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            ControlBox = false;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(11, 62, 72);
            DoubleBuffered = true;
            ResizeRedraw = true;
            KeyPreview = true;

            appearTimer = new System.Windows.Forms.Timer();
            appearTimer.Interval = 15;
            appearTimer.Tick += AppearTick;

            sourceLabel = CreateLabel("SourceWord", 19.0F, FontStyle.Bold, Color.White);
            sourceLabel.AccessibleName = "英文单词";

            phoneticLabel = CreateLabel("Phonetic", 9.0F, FontStyle.Regular, Color.FromArgb(203, 226, 235));
            phoneticLabel.AccessibleName = "音标";

            partOfSpeechPill = new PillLabel();
            partOfSpeechPill.Name = "PartOfSpeech";
            partOfSpeechPill.AccessibleName = "词性";
            partOfSpeechPill.ForeColor = Color.FromArgb(235, 253, 250);
            partOfSpeechPill.FillColor = Color.FromArgb(74, 255, 255, 255);
            partOfSpeechPill.BorderColor = Color.FromArgb(56, 255, 255, 255);

            providerPill = new PillLabel();
            providerPill.Name = "Provider";
            providerPill.AccessibleName = "翻译来源";
            providerPill.ForeColor = Color.FromArgb(225, 242, 255);
            providerPill.FillColor = Color.FromArgb(45, 177, 232, 255);
            providerPill.BorderColor = Color.FromArgb(38, 255, 255, 255);

            translationLabel = CreateLabel("ChineseTranslation", 13.0F, FontStyle.Bold, Color.FromArgb(245, 255, 253));
            translationLabel.AccessibleName = "简体中文释义";

            explanationCaption = CreateLabel("EnglishCaption", 7.5F, FontStyle.Bold, Color.FromArgb(154, 218, 225));
            explanationCaption.Text = "ENGLISH EXPLANATION";
            explanationCaption.AccessibleName = "英文解释标题";

            explanationLabel = CreateLabel("EnglishExplanation", 9.5F, FontStyle.Regular, Color.FromArgb(230, 242, 246));
            explanationLabel.AccessibleName = "英文解释";

            usageCaption = CreateLabel("UsageCaption", 7.5F, FontStyle.Bold, Color.FromArgb(179, 244, 222));
            usageCaption.Text = "REAL-LIFE USAGE  ·  生活用法";
            usageCaption.AccessibleName = "实际生活用法标题";

            usageLabel = CreateLabel("UsageText", 9.0F, FontStyle.Regular, Color.FromArgb(239, 252, 248));
            usageLabel.AccessibleName = "实际生活用法";

            speakButton = CreateButton("🔊", "朗读英文单词", ButtonTone.Glass);
            explainButton = CreateButton("听解释", "朗读英文解释", ButtonTone.Glass);
            aiButton = CreateButton("✦ AI 用法", "使用已配置的 AI 生成实际生活用法", ButtonTone.Accent);
            moreButton = CreateButton("详细", "在完整窗口中查看详细内容", ButtonTone.Glass);
            pauseButton = CreateButton("暂停", "暂停鼠标点译", ButtonTone.Glass);
            closeButton = CreateButton("关闭", "关闭这张词典卡片", ButtonTone.Glass);

            speakButton.Font = new Font("Segoe UI Symbol", 10.5F, FontStyle.Regular, GraphicsUnit.Point);
            partOfSpeechPill.Font = new Font("Segoe UI Semibold", 8.0F, FontStyle.Bold, GraphicsUnit.Point);
            providerPill.Font = new Font("Segoe UI Semibold", 7.0F, FontStyle.Bold, GraphicsUnit.Point);
            explainButton.Font = new Font("Microsoft YaHei UI", 8.0F, FontStyle.Regular, GraphicsUnit.Point);
            aiButton.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold, GraphicsUnit.Point);
            moreButton.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            pauseButton.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            closeButton.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            sourceLabel.TabIndex = 0;
            speakButton.TabIndex = 1;
            explainButton.TabIndex = 2;
            aiButton.TabIndex = 3;
            moreButton.TabIndex = 4;
            pauseButton.TabIndex = 5;
            closeButton.TabIndex = 6;

            speakButton.Click += delegate { Raise(SpeakRequested); };
            explainButton.Click += delegate { Raise(ExplainRequested); };
            aiButton.Click += delegate
            {
                if (aiBusy) return;
                if (AiRequested != null) Raise(AiRequested);
                else Raise(MoreRequested);
            };
            moreButton.Click += delegate { Raise(MoreRequested); };
            pauseButton.Click += delegate
            {
                Raise(PauseRequested);
                Hide();
            };
            closeButton.Click += delegate { Dismiss(); };

            Controls.Add(sourceLabel);
            Controls.Add(phoneticLabel);
            Controls.Add(partOfSpeechPill);
            Controls.Add(providerPill);
            Controls.Add(translationLabel);
            Controls.Add(explanationCaption);
            Controls.Add(explanationLabel);
            Controls.Add(usageCaption);
            Controls.Add(usageLabel);
            Controls.Add(speakButton);
            Controls.Add(explainButton);
            Controls.Add(aiButton);
            Controls.Add(moreButton);
            Controls.Add(pauseButton);
            Controls.Add(closeButton);

            fullTextTip = new ToolTip();
            fullTextTip.AutoPopDelay = 15000;
            fullTextTip.InitialDelay = 450;
            fullTextTip.ReshowDelay = 100;
            fullTextTip.ShowAlways = true;

            currentPartOfSpeech = "词条";
            currentProvider = "LOCAL OCR";
            ApplyContentLayout();
        }

        private Label CreateLabel(string name, float size, FontStyle style, Color colour)
        {
            Label label = new Label();
            label.Name = name;
            label.Font = new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
            label.ForeColor = colour;
            label.BackColor = Color.Transparent;
            label.AutoEllipsis = true;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.UseMnemonic = false;
            return label;
        }

        private BubbleButton CreateButton(string text, string accessibleName, ButtonTone tone)
        {
            BubbleButton button = new BubbleButton();
            button.Text = text;
            button.AccessibleName = accessibleName;
            button.AccessibleDescription = accessibleName;
            button.Tone = tone;
            button.TabStop = true;
            return button;
        }

        /// <summary>Shows a fresh local or AI translation and resets any prior enrichment.</summary>
        public void ShowResult(string english, TranslationResult result, string lineText, Point screenPoint)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, TranslationResult, string, Point>(ShowResult), english, result, lineText, screenPoint);
                return;
            }

            currentText = english == null ? String.Empty : english.Trim();
            currentLineText = lineText == null ? String.Empty : lineText.Trim();
            currentResult = result;
            currentPartOfSpeech = InferPartOfSpeech(currentText, result);
            currentUsage = BuildUsage(result);
            currentProvider = ProviderDisplay(result == null ? String.Empty : result.Provider);
            hasAiUsage = result != null && !String.Equals(result.Provider, "offline", StringComparison.OrdinalIgnoreCase) &&
                         !String.IsNullOrWhiteSpace(currentUsage);
            wideLayout = result != null &&
                         (String.Equals(result.MatchKind, "ai_pending", StringComparison.Ordinal) ||
                          String.Equals(result.MatchKind, "ai_sentence", StringComparison.Ordinal));
            aiBusy = false;

            sourceLabel.Text = FirstNonEmpty(currentText, "English");
            phoneticLabel.Text = result == null || String.IsNullOrWhiteSpace(result.Phonetic)
                ? String.Empty
                : FormatPhonetic(result.Phonetic);
            partOfSpeechPill.Text = currentPartOfSpeech;
            providerPill.Text = currentProvider;
            translationLabel.Text = result == null
                ? "暂无中文释义"
                : FirstNonEmpty(result.Translation, result.MeaningZh, "暂无中文释义");
            explanationLabel.Text = result == null
                ? "No English explanation is available."
                : (result.SimpleEnglish == null ? String.Empty : result.SimpleEnglish.Trim());
            usageLabel.Text = currentUsage;

            speakButton.Enabled = !String.IsNullOrWhiteSpace(currentText);
            explainButton.Enabled = result != null && !String.IsNullOrWhiteSpace(result.SimpleEnglish);
            aiButton.Enabled = result != null;
            aiButton.Text = hasAiUsage ? "✦ AI 已补充" : "✦ AI 用法";
            moreButton.Enabled = result != null;
            UpdateToolTips();
            ShowNear(screenPoint);
        }

        public void ShowMessage(string message, Point screenPoint)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, Point>(ShowMessage), message, screenPoint);
                return;
            }

            currentText = String.Empty;
            currentLineText = String.Empty;
            currentPartOfSpeech = "OCR";
            currentUsage = String.Empty;
            currentProvider = "LOCAL OCR";
            currentResult = null;
            hasAiUsage = false;
            wideLayout = false;
            aiBusy = false;

            sourceLabel.Text = "正在识别  Reading…";
            phoneticLabel.Text = String.Empty;
            partOfSpeechPill.Text = currentPartOfSpeech;
            providerPill.Text = currentProvider;
            translationLabel.Text = String.IsNullOrWhiteSpace(message) ? "请稍候…" : message.Trim();
            explanationLabel.Text = "Right-double-click the centre of an English word.";
            usageLabel.Text = String.Empty;
            speakButton.Enabled = false;
            explainButton.Enabled = false;
            aiButton.Enabled = false;
            aiButton.Text = "✦ AI 用法";
            moreButton.Enabled = false;
            UpdateToolTips();
            ShowNear(screenPoint);
        }

        /// <summary>
        /// Applies a completed Gemini/DeepSeek enrichment to the currently displayed word.
        /// Empty values preserve existing content. This method is safe to call after an await.
        /// </summary>
        public void SetAiEnrichment(
            string partOfSpeech,
            string englishExplanation,
            string usageEnglish,
            string usageChinese,
            string providerName)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, string, string, string, string>(SetAiEnrichment),
                    partOfSpeech, englishExplanation, usageEnglish, usageChinese, providerName);
                return;
            }
            ApplyAiEnrichment(partOfSpeech, englishExplanation, usageEnglish, usageChinese, providerName);
        }

        /// <summary>
        /// Applies AI content only if the bubble still shows expectedText. This avoids a late
        /// network response replacing a newer lookup. Returns false for a stale response.
        /// </summary>
        public bool TrySetAiEnrichment(
            string expectedText,
            string partOfSpeech,
            string englishExplanation,
            string usageEnglish,
            string usageChinese,
            string providerName)
        {
            if (IsDisposed) return false;
            if (InvokeRequired)
            {
                return (bool)Invoke(new Func<string, string, string, string, string, string, bool>(TrySetAiEnrichment),
                    expectedText, partOfSpeech, englishExplanation, usageEnglish, usageChinese, providerName);
            }
            if (!String.Equals(CurrentText, expectedText == null ? String.Empty : expectedText.Trim(),
                    StringComparison.OrdinalIgnoreCase)) return false;
            ApplyAiEnrichment(partOfSpeech, englishExplanation, usageEnglish, usageChinese, providerName);
            return true;
        }

        /// <summary>
        /// Replaces the complete pending/local result with a finished AI translation, but only
        /// while the bubble still belongs to the same source text. Long-sentence selection uses
        /// this path because AI must provide the Chinese translation as well as the explanation.
        /// </summary>
        public bool TrySetAiResult(string expectedText, TranslationResult result)
        {
            if (IsDisposed || result == null) return false;
            if (InvokeRequired)
            {
                return (bool)Invoke(new Func<string, TranslationResult, bool>(TrySetAiResult),
                    expectedText, result);
            }
            if (!String.Equals(CurrentText, expectedText == null ? String.Empty : expectedText.Trim(),
                    StringComparison.OrdinalIgnoreCase)) return false;

            currentResult = CloneResult(result);
            currentPartOfSpeech = InferPartOfSpeech(currentText, currentResult);
            currentUsage = BuildUsage(currentResult);
            currentProvider = ProviderDisplay(currentResult.Provider);
            hasAiUsage = !String.Equals(currentResult.Provider, "offline", StringComparison.OrdinalIgnoreCase) &&
                         !String.IsNullOrWhiteSpace(currentUsage);
            wideLayout = String.Equals(currentResult.MatchKind, "ai_sentence", StringComparison.Ordinal) ||
                         String.Equals(currentResult.MatchKind, "ai_pending", StringComparison.Ordinal);
            aiBusy = false;

            phoneticLabel.Text = String.IsNullOrWhiteSpace(currentResult.Phonetic)
                ? String.Empty
                : FormatPhonetic(currentResult.Phonetic);
            partOfSpeechPill.Text = currentPartOfSpeech;
            providerPill.Text = currentProvider;
            translationLabel.Text = FirstNonEmpty(
                currentResult.Translation,
                currentResult.MeaningZh,
                "暂无中文释义");
            // No filler here: sentence-only results carry no explanation, and an empty
            // value lets the whole ENGLISH EXPLANATION section collapse.
            explanationLabel.Text = currentResult.SimpleEnglish == null
                ? String.Empty
                : currentResult.SimpleEnglish.Trim();
            usageLabel.Text = currentUsage;

            speakButton.Enabled = !String.IsNullOrWhiteSpace(currentText);
            explainButton.Enabled = !String.IsNullOrWhiteSpace(currentResult.SimpleEnglish);
            aiButton.Enabled = true;
            aiButton.Text = hasAiUsage ? "✦ AI 已补充" : "✦ AI 用法";
            moreButton.Enabled = true;
            UpdateToolTips();
            ApplyContentLayout();
            Invalidate(true);
            return true;
        }

        /// <summary>Updates the AI action state while Gemini or DeepSeek is working.</summary>
        public void SetAiBusy(bool busy, string statusText)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<bool, string>(SetAiBusy), busy, statusText);
                return;
            }
            aiBusy = busy;
            aiButton.Enabled = !busy && currentResult != null;
            aiButton.Text = busy ? "AI 生成中…" : (hasAiUsage ? "✦ AI 已补充" : "✦ AI 用法");
            if (!String.IsNullOrWhiteSpace(statusText)) fullTextTip.SetToolTip(aiButton, statusText.Trim());
            Invalidate();
        }

        /// <summary>
        /// Surfaces an AI failure inside the bubble. A long-sentence card that still says
        /// "AI 正在翻译…" must be replaced by the actual error, not just a tooltip.
        /// </summary>
        public void ShowAiFailure(string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(ShowAiFailure), message);
                return;
            }
            string text = String.IsNullOrWhiteSpace(message)
                ? "AI 暂未完成，请稍后重试。 / The AI request did not complete."
                : message.Trim();
            aiBusy = false;
            aiButton.Enabled = currentResult != null;
            aiButton.Text = hasAiUsage ? "✦ AI 已补充" : "✦ AI 用法";
            fullTextTip.SetToolTip(aiButton, text);
            if (currentResult != null &&
                String.Equals(currentResult.MatchKind, "ai_pending", StringComparison.Ordinal))
            {
                currentResult.MatchKind = "ai_failed";
                translationLabel.Text = text;
                explanationLabel.Text = String.Empty;
                UpdateToolTips();
                ApplyContentLayout();
                Invalidate(true);
                return;
            }
            Invalidate();
        }

        private void ApplyAiEnrichment(
            string partOfSpeech,
            string englishExplanation,
            string usageEnglish,
            string usageChinese,
            string providerName)
        {
            if (!String.IsNullOrWhiteSpace(partOfSpeech)) currentPartOfSpeech = NormalisePartOfSpeech(partOfSpeech);
            if (!String.IsNullOrWhiteSpace(englishExplanation)) explanationLabel.Text = englishExplanation.Trim();

            string usage = JoinUsage(usageEnglish, usageChinese);
            if (!String.IsNullOrWhiteSpace(usage)) currentUsage = usage;
            if (!String.IsNullOrWhiteSpace(providerName)) currentProvider = ProviderDisplay(providerName);
            if (currentResult != null)
            {
                if (!String.IsNullOrWhiteSpace(partOfSpeech)) currentResult.PartOfSpeech = partOfSpeech.Trim();
                if (!String.IsNullOrWhiteSpace(englishExplanation)) currentResult.SimpleEnglish = englishExplanation.Trim();
                if (!String.IsNullOrWhiteSpace(usageEnglish)) currentResult.PracticalUsageEn = usageEnglish.Trim();
                if (!String.IsNullOrWhiteSpace(usageChinese)) currentResult.PracticalUsageZh = usageChinese.Trim();
                if (!String.IsNullOrWhiteSpace(providerName))
                {
                    currentResult.Provider = providerName.Trim().ToLowerInvariant();
                    currentResult.MatchKind = "ai_enriched";
                    currentResult.MeaningZh = currentResult.Provider == "deepseek"
                        ? "已使用 DeepSeek 补充词性与生活用法；截图未上传。"
                        : "已使用 Gemini 补充词性与生活用法；截图未上传。";
                }
            }
            hasAiUsage = !String.IsNullOrWhiteSpace(currentUsage);
            aiBusy = false;

            partOfSpeechPill.Text = currentPartOfSpeech;
            providerPill.Text = currentProvider;
            usageLabel.Text = currentUsage;
            aiButton.Enabled = currentResult != null;
            aiButton.Text = hasAiUsage ? "✦ AI 已补充" : "✦ AI 用法";
            UpdateToolTips();
            ApplyContentLayout();
            Invalidate(true);
        }

        private static TranslationResult CloneResult(TranslationResult source)
        {
            if (source == null) return null;
            TranslationResult clone = new TranslationResult();
            clone.Direction = source.Direction;
            clone.SourceLanguage = source.SourceLanguage;
            clone.Translation = source.Translation;
            clone.MeaningZh = source.MeaningZh;
            clone.SimpleEnglish = source.SimpleEnglish;
            clone.SpeakText = source.SpeakText;
            clone.ExampleEn = source.ExampleEn;
            clone.ExampleZh = source.ExampleZh;
            clone.SingaporeNote = source.SingaporeNote;
            clone.Provider = source.Provider;
            clone.MatchKind = source.MatchKind;
            clone.Phonetic = source.Phonetic;
            clone.PartOfSpeech = source.PartOfSpeech;
            clone.PracticalUsageEn = source.PracticalUsageEn;
            clone.PracticalUsageZh = source.PracticalUsageZh;
            clone.CoveredWords = source.CoveredWords;
            clone.TotalWords = source.TotalWords;
            return clone;
        }

        private void ShowNear(Point point)
        {
            // The low-level hook reports physical desktop pixels. On .NET Framework at 150%+
            // DPI, Screen.WorkingArea can still be logical/virtualized for a newly shown form,
            // which clips a physically sized bubble. Read the monitor work area from Win32 so
            // the anchor, form size, and boundary checks all use physical pixels.
            Rectangle area = GetPhysicalWorkingArea(point);
            bool firstShow = !Visible;

            // Moving the hidden/non-activating tool window first lets GetDpiForWindow
            // resolve the target monitor before logical dimensions are converted. When the
            // bubble is already visible, skip the pre-move so it never visibly jumps.
            if (firstShow) Location = point;
            UpdateDpi();

            tailOnTop = false;
            ApplyContentLayout();
            bool fitsAbove = point.Y - Height - L(4) >= area.Top;
            if (!fitsAbove)
            {
                tailOnTop = true;
                ApplyContentLayout();
            }

            int x = point.X - L(34);
            int y = fitsAbove ? point.Y - Height - L(4) : point.Y + L(62);
            x = Math.Max(area.Left + L(4), Math.Min(x, area.Right - Width - L(4)));
            y = Math.Max(area.Top + L(4), Math.Min(y, area.Bottom - Height - L(4)));

            tailCentre = Math.Max(L(25), Math.Min(point.X - x, Width - L(25)));
            Location = new Point(x, y);
            UpdateBubbleRegion();
            if (firstShow)
            {
                BeginAppear(y);
                Show();
            }
            Invalidate(true);
        }

        /// <summary>A brief fade-and-rise entrance (~120 ms) instead of an abrupt pop-in.</summary>
        private void BeginAppear(int settledTop)
        {
            appearStep = 0;
            appearBaseTop = settledTop;
            try
            {
                Opacity = 0D;
                Top = settledTop + L(10);
                appearTimer.Start();
            }
            catch
            {
                Opacity = 1D;
                Top = settledTop;
            }
        }

        private void AppearTick(object sender, EventArgs e)
        {
            appearStep++;
            double progress = Math.Min(1.0, appearStep / 8.0);
            double eased = 1.0 - Math.Pow(1.0 - progress, 3.0);
            try
            {
                Opacity = eased;
                Top = appearBaseTop + (int)Math.Round(L(10) * (1.0 - eased));
                if (progress >= 1.0)
                {
                    appearTimer.Stop();
                    Opacity = 1D;
                    Top = appearBaseTop;
                }
            }
            catch
            {
                appearTimer.Stop();
                try { Opacity = 1D; } catch { }
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible && appearTimer != null)
            {
                // A hide during the entrance must never leave a half-transparent bubble.
                appearTimer.Stop();
                try { Opacity = 1D; } catch { }
            }
        }

        private static Rectangle GetPhysicalWorkingArea(Point point)
        {
            MonitorPoint nativePoint = new MonitorPoint();
            nativePoint.x = point.X;
            nativePoint.y = point.Y;
            IntPtr monitor = MonitorFromPoint(nativePoint, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero)
            {
                MonitorInfo information = new MonitorInfo();
                information.size = Marshal.SizeOf(typeof(MonitorInfo));
                if (GetMonitorInfo(monitor, ref information))
                {
                    return Rectangle.FromLTRB(
                        information.work.left,
                        information.work.top,
                        information.work.right,
                        information.work.bottom);
                }
            }
            return Screen.FromPoint(point).WorkingArea;
        }

        private void ApplyContentLayout()
        {
            SuspendLayout();
            int width = L(wideLayout ? 468 : LogicalWidth);
            int tail = L(LogicalTailHeight);
            int bodyTop = tailOnTop ? tail : 0;
            int pad = L(18);
            int innerWidth = width - (pad * 2);
            int y = bodyTop + L(15);

            int headerButton = L(36);
            int headlineWidth = innerWidth - headerButton - L(9);
            // A long word or a whole selected sentence must stay readable instead of
            // being clipped into "Projec…": step the headline font down to fit one
            // line, and fall back to two wrapped lines for full sentences.
            float headlineSize;
            bool twoLineHeadline;
            ChooseHeadlineFit(headlineWidth, out headlineSize, out twoLineHeadline);
            ApplyHeadlineFont(headlineSize);
            int headlineHeight = twoLineHeadline ? L(44) : L(32);
            sourceLabel.SetBounds(pad, y, headlineWidth, headlineHeight);
            speakButton.CornerRadius = L(18);
            speakButton.SetBounds(width - pad - headerButton, y - L(1), headerButton, headerButton);
            y += headlineHeight + L(5);

            int posWidth = Math.Max(L(44), Math.Min(L(104), MeasureSingleLine(partOfSpeechPill.Text, partOfSpeechPill.Font) + L(18)));
            partOfSpeechPill.SetBounds(pad, y, posWidth, L(23));

            // Size the provider pill first so the phonetic label can never run underneath it.
            int providerWidth = Math.Max(L(66), Math.Min(L(104), MeasureSingleLine(providerPill.Text, providerPill.Font) + L(16)));
            providerPill.SetBounds(width - pad - providerWidth, y, providerWidth, L(23));
            phoneticLabel.SetBounds(
                pad + posWidth + L(9), y,
                Math.Max(0, innerWidth - posWidth - providerWidth - L(18)), L(23));
            y += L(32);

            // Sentence-only cards have no explanation/usage sections, so the freed
            // space goes to the translation itself: up to ~16 lines instead of 4.
            bool explanationPlanned = !String.IsNullOrWhiteSpace(explanationLabel.Text);
            int translationCap = explanationPlanned ? 96 : 360;
            int translationHeight = MeasureWrappedHeight(translationLabel.Text, translationLabel.Font, innerWidth, 30, translationCap);
            translationLabel.SetBounds(pad, y, innerWidth, translationHeight);
            y += translationHeight + L(11);

            dividerBounds = new Rectangle(pad, y, innerWidth, Math.Max(1, L(1)));
            y += L(12);

            // No filler: when there is no English explanation (AI pending/failed cards),
            // the whole section collapses so the bubble stays as small as possible.
            bool showExplanation = !String.IsNullOrWhiteSpace(explanationLabel.Text);
            explanationCaption.Visible = showExplanation;
            explanationLabel.Visible = showExplanation;
            explainButton.Visible = showExplanation;
            if (showExplanation)
            {
                explanationCaption.SetBounds(pad, y, innerWidth - L(82), L(17));
                explainButton.CornerRadius = L(10);
                explainButton.SetBounds(width - pad - L(70), y - L(3), L(70), L(24));
                y += L(20);

                int explanationHeight = MeasureWrappedHeight(explanationLabel.Text, explanationLabel.Font, innerWidth, 22, 84);
                explanationLabel.SetBounds(pad, y, innerWidth, explanationHeight);
                y += explanationHeight + L(11);
            }

            bool showUsage = !String.IsNullOrWhiteSpace(currentUsage);
            usageCaption.Visible = showUsage;
            usageLabel.Visible = showUsage;
            usageCardBounds = Rectangle.Empty;
            if (showUsage)
            {
                int usageTextHeight = MeasureWrappedHeight(currentUsage, usageLabel.Font, innerWidth - L(24), 22, 88);
                int cardHeight = L(35) + usageTextHeight + L(10);
                usageCardBounds = new Rectangle(pad, y, innerWidth, cardHeight);
                usageCaption.SetBounds(pad + L(12), y + L(8), innerWidth - L(24), L(17));
                usageLabel.SetBounds(pad + L(12), y + L(27), innerWidth - L(24), usageTextHeight);
                y += cardHeight + L(12);
            }

            int gap = L(6);
            int buttonHeight = L(32);
            int aiWidth = L(108);
            int remaining = innerWidth - aiWidth - (gap * 3);
            int smallWidth = remaining / 3;
            aiButton.CornerRadius = L(11);
            moreButton.CornerRadius = L(11);
            pauseButton.CornerRadius = L(11);
            closeButton.CornerRadius = L(11);

            int buttonX = pad;
            aiButton.SetBounds(buttonX, y, aiWidth, buttonHeight);
            buttonX += aiWidth + gap;
            moreButton.SetBounds(buttonX, y, smallWidth, buttonHeight);
            buttonX += smallWidth + gap;
            pauseButton.SetBounds(buttonX, y, smallWidth, buttonHeight);
            buttonX += smallWidth + gap;
            closeButton.SetBounds(buttonX, y, width - pad - buttonX, buttonHeight);
            y += buttonHeight + L(15);

            int logicalMinimum = showUsage ? 316 : (showExplanation ? 238 : 172);
            int bodyHeight = Math.Max(L(logicalMinimum), y - bodyTop);
            ClientSize = new Size(width, bodyHeight + tail);
            ResumeLayout(false);
            UpdateBubbleRegion();
        }

        private void UpdateToolTips()
        {
            fullTextTip.SetToolTip(sourceLabel, sourceLabel.Text);
            fullTextTip.SetToolTip(translationLabel, translationLabel.Text);
            fullTextTip.SetToolTip(explanationLabel, explanationLabel.Text);
            fullTextTip.SetToolTip(usageLabel, usageLabel.Text);
            fullTextTip.SetToolTip(providerPill, "内容来源：" + currentProvider);
            fullTextTip.SetToolTip(aiButton, hasAiUsage
                ? "已补充 AI 生活用法；点击可重新生成。"
                : "使用 Gemini 或 DeepSeek 补充真实生活用法。只有点击后才会联网。");
        }

        private void Dismiss()
        {
            Hide();
            Raise(CloseRequested);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Dismiss();
                return true;
            }
            if (keyData == (Keys.Control | Keys.Enter) && moreButton.Enabled)
            {
                Raise(MoreRequested);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (appearTimer != null)
                {
                    appearTimer.Stop();
                    appearTimer.Dispose();
                }
                if (fullTextTip != null) fullTextTip.Dispose();
                if (headlineFont != null)
                {
                    headlineFont.Dispose();
                    headlineFont = null;
                }
            }
            base.Dispose(disposing);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WmDpiChanged && IsHandleCreated)
            {
                UpdateDpi();
                ApplyContentLayout();
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(8, 56, 66));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using (GraphicsPath bubble = CreateBubblePath())
            using (LinearGradientBrush gradient = new LinearGradientBrush(
                ClientRectangle,
                Color.FromArgb(10, 104, 104),
                Color.FromArgb(60, 50, 126),
                LinearGradientMode.ForwardDiagonal))
            {
                ApplyBubbleGradientBlend(gradient);
                e.Graphics.FillPath(gradient, bubble);

                using (Pen border = new Pen(Color.FromArgb(84, 192, 238, 228), Math.Max(1.0F, L(1))))
                    e.Graphics.DrawPath(border, bubble);
            }

            // A faint glass highlight across the top edge gives the card physical depth.
            int shineTop = (tailOnTop ? L(LogicalTailHeight) : 0) + L(2);
            Rectangle shine = new Rectangle(L(22), shineTop, Math.Max(1, ClientSize.Width - L(44)), Math.Max(1, L(1)));
            using (LinearGradientBrush shineBrush = new LinearGradientBrush(
                shine, Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal))
            {
                ColorBlend shineBlend = new ColorBlend();
                shineBlend.Colors = new Color[]
                {
                    Color.FromArgb(0, 255, 255, 255),
                    Color.FromArgb(64, 255, 255, 255),
                    Color.FromArgb(0, 255, 255, 255)
                };
                shineBlend.Positions = new float[] { 0.0F, 0.5F, 1.0F };
                shineBrush.InterpolationColors = shineBlend;
                e.Graphics.FillRectangle(shineBrush, shine);
            }

            if (!dividerBounds.IsEmpty)
            {
                using (LinearGradientBrush divider = new LinearGradientBrush(
                    dividerBounds,
                    Color.FromArgb(8, 255, 255, 255),
                    Color.FromArgb(80, 137, 233, 220),
                    LinearGradientMode.Horizontal))
                    e.Graphics.FillRectangle(divider, dividerBounds);
            }

            if (!usageCardBounds.IsEmpty)
            {
                using (GraphicsPath card = RoundedRectangle(usageCardBounds, L(12)))
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(30, 224, 255, 245)))
                using (Pen outline = new Pen(Color.FromArgb(40, 203, 250, 229), Math.Max(1.0F, L(1))))
                {
                    e.Graphics.FillPath(fill, card);
                    e.Graphics.DrawPath(outline, card);
                }
            }
        }

        private static void ApplyBubbleGradientBlend(LinearGradientBrush gradient)
        {
            ColorBlend blend = new ColorBlend();
            blend.Colors = new Color[]
            {
                Color.FromArgb(9, 70, 78),
                Color.FromArgb(24, 62, 104),
                Color.FromArgb(58, 46, 118)
            };
            blend.Positions = new float[] { 0.0F, 0.55F, 1.0F };
            gradient.InterpolationColors = blend;
        }

        private void UpdateBubbleRegion()
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            using (GraphicsPath path = CreateBubblePath())
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
            Invalidate();
        }

        private GraphicsPath CreateBubblePath()
        {
            int tail = L(LogicalTailHeight);
            int bodyTop = tailOnTop ? tail : 0;
            int bodyBottom = ClientSize.Height - (tailOnTop ? 0 : tail);
            Rectangle body = new Rectangle(0, bodyTop, Math.Max(1, ClientSize.Width - 1), Math.Max(1, bodyBottom - bodyTop - 1));
            GraphicsPath path = RoundedRectangle(body, L(18));
            path.FillMode = FillMode.Winding;
            int centre = tailCentre <= 0 ? L(34) : tailCentre;
            int half = L(11);
            if (tailOnTop)
            {
                path.AddPolygon(new Point[]
                {
                    new Point(centre - half, bodyTop + 1),
                    new Point(centre, 0),
                    new Point(centre + half, bodyTop + 1)
                });
            }
            else
            {
                path.AddPolygon(new Point[]
                {
                    new Point(centre - half, bodyBottom - 1),
                    new Point(centre, ClientSize.Height - 1),
                    new Point(centre + half, bodyBottom - 1)
                });
            }
            return path;
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = Math.Max(2, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
            Rectangle arc = new Rectangle(bounds.Left, bounds.Top, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void UpdateDpi()
        {
            int dpi = 96;
            try
            {
                if (IsHandleCreated)
                {
                    uint nativeDpi = GetDpiForWindow(Handle);
                    if (nativeDpi >= 96 && nativeDpi <= 768) dpi = (int)nativeDpi;
                }
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            if (dpi == 96)
            {
                try
                {
                    using (Graphics graphics = CreateGraphics())
                    {
                        if (graphics.DpiX >= 96.0F) dpi = (int)Math.Round(graphics.DpiX);
                    }
                }
                catch { }
            }
            currentDpi = Math.Max(96, dpi);
        }

        private int L(int logical)
        {
            return Math.Max(logical == 0 ? 0 : 1, (int)Math.Round(logical * currentDpi / 96.0));
        }

        private int MeasureSingleLine(string value, Font font)
        {
            if (String.IsNullOrEmpty(value)) return 0;
            return TextRenderer.MeasureText(value, font, new Size(Int32.MaxValue, L(30)),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        }

        /// <summary>Picks the largest headline size (19 → 16 → 13.5 pt) whose single line fits.</summary>
        private void ChooseHeadlineFit(int availableWidth, out float size, out bool twoLines)
        {
            size = 19.0F;
            twoLines = false;
            string text = sourceLabel.Text;
            if (String.IsNullOrEmpty(text) || availableWidth <= 0) return;
            float[] ladder = new float[] { 19.0F, 16.0F, 13.5F };
            foreach (float candidate in ladder)
            {
                size = candidate;
                using (Font probe = new Font("Microsoft YaHei UI", candidate, FontStyle.Bold, GraphicsUnit.Point))
                {
                    if (MeasureSingleLine(text, probe) <= availableWidth) return;
                }
            }
            // Even the smallest size cannot fit one line (a full sentence): let the
            // label wrap to two lines; AutoEllipsis still trims a very long tail.
            twoLines = true;
        }

        private void ApplyHeadlineFont(float size)
        {
            if (Math.Abs(sourceLabel.Font.Size - size) < 0.1F) return;
            Font next = new Font("Microsoft YaHei UI", size, FontStyle.Bold, GraphicsUnit.Point);
            Font previous = headlineFont;
            sourceLabel.Font = next;
            headlineFont = next;
            if (previous != null) previous.Dispose();
        }

        private int MeasureWrappedHeight(string value, Font font, int width, int logicalMinimum, int logicalMaximum)
        {
            int minimum = L(logicalMinimum);
            int maximum = L(logicalMaximum);
            if (String.IsNullOrWhiteSpace(value)) return minimum;
            Size measured = TextRenderer.MeasureText(value, font, new Size(Math.Max(1, width), Int32.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            return Math.Max(minimum, Math.Min(maximum, measured.Height + L(2)));
        }

        private static string BuildUsage(TranslationResult result)
        {
            if (result == null) return String.Empty;
            string examples = JoinUsage(result.PracticalUsageEn, result.PracticalUsageZh);
            string exampleSentence = JoinUsage(result.ExampleEn, result.ExampleZh);
            if (!String.IsNullOrWhiteSpace(exampleSentence) &&
                !String.Equals(examples, exampleSentence, StringComparison.OrdinalIgnoreCase))
            {
                examples = String.IsNullOrWhiteSpace(examples)
                    ? exampleSentence
                    : examples + "\r\n" + exampleSentence;
            }
            if (!String.IsNullOrWhiteSpace(result.SingaporeNote))
            {
                examples = String.IsNullOrWhiteSpace(examples)
                    ? result.SingaporeNote.Trim()
                    : examples + "\r\n" + result.SingaporeNote.Trim();
            }
            return examples;
        }

        private static string JoinUsage(string english, string chinese)
        {
            string en = english == null ? String.Empty : english.Trim();
            string zh = chinese == null ? String.Empty : chinese.Trim();
            if (en.Length == 0) return zh;
            if (zh.Length == 0) return en;
            return en + "\r\n" + zh;
        }

        private static string FormatPhonetic(string value)
        {
            string text = value == null ? String.Empty : value.Trim();
            if (text.Length == 0) return String.Empty;
            if ((text.StartsWith("/") && text.EndsWith("/")) ||
                (text.StartsWith("[") && text.EndsWith("]"))) return text;
            return "/" + text + "/";
        }

        private static string InferPartOfSpeech(string english, TranslationResult result)
        {
            if (result != null && !String.IsNullOrWhiteSpace(result.PartOfSpeech))
                return NormalisePartOfSpeech(result.PartOfSpeech);
            string source = String.Empty;
            if (result != null)
                source = FirstNonEmpty(result.Translation, result.SimpleEnglish, result.MeaningZh);

            MatchCollection matches = Regex.Matches(source,
                @"(?im)(?:^|[\r\n;；])\s*(n|v|vt|vi|adj|adv|prep|pron|conj|interj|int|num|art|aux|modal|abbr)\s*\.");
            List<string> parts = new List<string>();
            foreach (Match match in matches)
            {
                string part = match.Groups[1].Value.ToLowerInvariant() + ".";
                if (!parts.Contains(part)) parts.Add(part);
                if (parts.Count == 3) break;
            }
            if (parts.Count > 0) return String.Join(" / ", parts.ToArray());
            return !String.IsNullOrWhiteSpace(english) && english.Trim().IndexOf(' ') >= 0 ? "词组" : "词条";
        }

        private static string NormalisePartOfSpeech(string value)
        {
            string text = Regex.Replace(value == null ? String.Empty : value.Trim(), @"\s+", " ");
            if (text.Length > 22) text = text.Substring(0, 21).TrimEnd() + "…";
            return FirstNonEmpty(text, "词条");
        }

        private static string ProviderDisplay(string provider)
        {
            string value = provider == null ? String.Empty : provider.Trim();
            if (value.Equals("gemini", StringComparison.OrdinalIgnoreCase)) return "GEMINI AI";
            if (value.Equals("deepseek", StringComparison.OrdinalIgnoreCase)) return "DEEPSEEK AI";
            if (value.Equals("offline", StringComparison.OrdinalIgnoreCase)) return "OFFLINE";
            if (value.Length == 0) return "LOCAL OCR";
            value = Regex.Replace(value, @"\s+", " ").ToUpperInvariant();
            return value.Length <= 15 ? value : value.Substring(0, 14) + "…";
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorPoint
        {
            internal int x;
            internal int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorRectangle
        {
            internal int left;
            internal int top;
            internal int right;
            internal int bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfo
        {
            internal int size;
            internal MonitorRectangle monitor;
            internal MonitorRectangle work;
            internal uint flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(MonitorPoint point, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo information);

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!String.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return String.Empty;
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);

        private enum ButtonTone
        {
            Glass,
            Accent
        }

        private sealed class BubbleButton : Button
        {
            private bool hovering;
            private bool pressing;
            private ButtonTone tone;

            internal int CornerRadius { get; set; }
            internal ButtonTone Tone
            {
                get { return tone; }
                set { tone = value; Invalidate(); }
            }

            internal BubbleButton()
            {
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                UseVisualStyleBackColor = false;
                // ButtonBase does not composite semi-transparent BackColor reliably on a
                // layered, custom-painted form. At high DPI that left black corners and
                // pixels from controls that previously occupied the same area. The whole
                // surface is painted opaquely in OnPaint before the glass capsule is drawn.
                BackColor = Color.FromArgb(19, 83, 105);
                ForeColor = Color.White;
                Cursor = Cursors.Hand;
                TabStop = true;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                         ControlStyles.Opaque, true);
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                hovering = true;
                Invalidate();
                base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                hovering = false;
                pressing = false;
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    pressing = true;
                    Invalidate();
                }
                base.OnMouseDown(e);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                pressing = false;
                Invalidate();
                base.OnMouseUp(e);
            }

            protected override void OnEnabledChanged(EventArgs e)
            {
                base.OnEnabledChanged(e);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                // Recreate the parent's diagonal background in this control's coordinate
                // system. This gives every pixel an opaque, deterministic base and avoids
                // WinForms' sibling-buffer artefacts for transparent Button controls.
                Rectangle parentGradient = Parent == null
                    ? ClientRectangle
                    : new Rectangle(-Left, -Top, Math.Max(1, Parent.ClientSize.Width), Math.Max(1, Parent.ClientSize.Height));
                using (LinearGradientBrush background = new LinearGradientBrush(
                    parentGradient,
                    Color.FromArgb(10, 104, 104),
                    Color.FromArgb(60, 50, 126),
                    LinearGradientMode.ForwardDiagonal))
                {
                    QuickTranslationPopup.ApplyBubbleGradientBlend(background);
                    e.Graphics.FillRectangle(background, ClientRectangle);
                }
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
                int radius = CornerRadius > 0 ? CornerRadius : Math.Max(5, Height / 3);
                using (GraphicsPath path = RoundedRectangle(bounds, radius))
                {
                    Color fill;
                    Color border;
                    if (!Enabled)
                    {
                        fill = Color.FromArgb(24, 255, 255, 255);
                        border = Color.FromArgb(20, 255, 255, 255);
                    }
                    else if (tone == ButtonTone.Accent)
                    {
                        fill = pressing
                            ? Color.FromArgb(255, 26, 168, 150)
                            : hovering ? Color.FromArgb(255, 46, 214, 188) : Color.FromArgb(242, 36, 192, 172);
                        border = Color.FromArgb(150, 193, 255, 240);
                    }
                    else
                    {
                        fill = pressing
                            ? Color.FromArgb(96, 255, 255, 255)
                            : hovering ? Color.FromArgb(66, 255, 255, 255) : Color.FromArgb(36, 255, 255, 255);
                        border = Color.FromArgb(54, 220, 246, 246);
                    }

                    using (SolidBrush brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
                    using (Pen pen = new Pen(border, 1.0F)) e.Graphics.DrawPath(pen, path);
                }

                Color textColour = Enabled ? Color.White : Color.FromArgb(126, 222, 232, 235);
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, textColour,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                if (Focused && ShowFocusCues)
                {
                    Rectangle focus = Rectangle.Inflate(ClientRectangle, -4, -4);
                    ControlPaint.DrawFocusRectangle(e.Graphics, focus, Color.White, Color.Transparent);
                }
            }
        }

        private sealed class PillLabel : Label
        {
            internal Color FillColor { get; set; }
            internal Color BorderColor { get; set; }

            internal PillLabel()
            {
                BackColor = Color.Transparent;
                TextAlign = ContentAlignment.MiddleCenter;
                AutoEllipsis = true;
                UseMnemonic = false;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
                using (GraphicsPath path = RoundedRectangle(bounds, Math.Max(4, Height / 2)))
                using (SolidBrush fill = new SolidBrush(FillColor))
                using (Pen border = new Pen(BorderColor, 1.0F))
                {
                    e.Graphics.FillPath(fill, path);
                    e.Graphics.DrawPath(border, path);
                }
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }
    }
}
