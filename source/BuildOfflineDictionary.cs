using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace SGFloatingTranslator.DictionaryBuild
{
    internal static class BuildOfflineDictionary
    {
        private sealed class Record
        {
            internal string Word;
            internal string Phonetic;
            internal string Definition;
            internal string Translation;
            internal string Exchange;
            internal int Score;
        }

        private static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("Usage: BuildOfflineDictionary.exe <ecdict.csv> <offline_ecdict_core.tsv.gz>");
                return 2;
            }

            string input = Path.GetFullPath(args[0]);
            string output = Path.GetFullPath(args[1]);
            if (!File.Exists(input))
            {
                Console.Error.WriteLine("Input not found: " + input);
                return 2;
            }

            Dictionary<string, Record> selected = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
            int scanned = 0;
            using (TextFieldParser parser = new TextFieldParser(input, Encoding.UTF8, true))
            {
                parser.TextFieldType = FieldType.Delimited;
                parser.SetDelimiters(",");
                parser.HasFieldsEnclosedInQuotes = true;
                if (!parser.EndOfData) parser.ReadFields();

                while (!parser.EndOfData)
                {
                    string[] row;
                    try { row = parser.ReadFields(); }
                    catch (MalformedLineException) { continue; }
                    scanned++;
                    if (row == null || row.Length < 13) continue;

                    string word = CleanHeadword(row[0]);
                    string definition = CleanField(row[2], 4, 700, false);
                    string translation = CleanField(row[3], 7, 900, true);
                    if (!IsUsableHeadword(word) || definition.Length == 0 || translation.Length == 0) continue;

                    int collins = ParseInt(row[5]);
                    int oxford = ParseInt(row[6]);
                    string tags = row[7] == null ? String.Empty : row[7].Trim();
                    int bnc = ParseInt(row[8]);
                    int frq = ParseInt(row[9]);
                    bool core = oxford > 0 || collins > 0 || tags.Length > 0 ||
                                (bnc > 0 && bnc <= 50000) || (frq > 0 && frq <= 50000);
                    if (!core) continue;

                    int score = (oxford > 0 ? 1000000 : 0) + collins * 100000 +
                                (tags.Length > 0 ? 50000 : 0) + FrequencyScore(bnc) + FrequencyScore(frq);
                    Record candidate = new Record();
                    candidate.Word = word;
                    candidate.Phonetic = CleanField(row[1], 1, 120, false);
                    candidate.Definition = definition;
                    candidate.Translation = translation;
                    candidate.Exchange = CleanExchange(row[10]);
                    candidate.Score = score;

                    string key = NormalizeKey(word);
                    Record existing;
                    if (!selected.TryGetValue(key, out existing) || candidate.Score > existing.Score)
                        selected[key] = candidate;
                }
            }

            List<Record> records = new List<Record>(selected.Values);
            records.Sort(delegate(Record left, Record right)
            {
                return StringComparer.OrdinalIgnoreCase.Compare(left.Word, right.Word);
            });

            Directory.CreateDirectory(Path.GetDirectoryName(output));
            using (FileStream file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None))
            using (GZipStream gzip = new GZipStream(file, CompressionLevel.Optimal))
            using (StreamWriter writer = new StreamWriter(gzip, new UTF8Encoding(false)))
            {
                writer.WriteLine("#SGFT-ECDICT-1\t2026-07-22\t" + records.Count.ToString(CultureInfo.InvariantCulture));
                foreach (Record record in records)
                {
                    writer.Write(ToBase64(record.Word));
                    writer.Write('\t');
                    writer.Write(ToBase64(record.Phonetic));
                    writer.Write('\t');
                    writer.Write(ToBase64(record.Definition));
                    writer.Write('\t');
                    writer.Write(ToBase64(record.Translation));
                    writer.Write('\t');
                    writer.Write(ToBase64(record.Exchange));
                    writer.WriteLine();
                }
            }

            Console.WriteLine("Scanned: " + scanned.ToString("N0", CultureInfo.InvariantCulture));
            Console.WriteLine("Core entries: " + records.Count.ToString("N0", CultureInfo.InvariantCulture));
            Console.WriteLine("Output: " + output);
            return 0;
        }

        private static int ParseInt(string value)
        {
            int parsed;
            return Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        private static int FrequencyScore(int rank)
        {
            return rank > 0 && rank <= 50000 ? 50000 - rank : 0;
        }

        private static string CleanHeadword(string value)
        {
            return String.IsNullOrWhiteSpace(value)
                ? String.Empty
                : Regex.Replace(value.Trim().Normalize(NormalizationForm.FormKC), @"\s+", " ");
        }

        private static bool IsUsableHeadword(string word)
        {
            if (word.Length == 0 || word.Length > 80) return false;
            if (!Regex.IsMatch(word, "[A-Za-z]")) return false;
            if (word.StartsWith("-", StringComparison.Ordinal) || word.StartsWith(".", StringComparison.Ordinal)) return false;
            return Regex.IsMatch(word, @"^[A-Za-z0-9][A-Za-z0-9 À-ÖØ-öø-ÿ'’.,&/+()\-]*$");
        }

        private static string NormalizeKey(string value)
        {
            string key = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            key = key.Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u2013', '-').Replace('\u2014', '-');
            key = Regex.Replace(key, @"\s+", " ").Trim();
            return key;
        }

        private static string CleanField(string value, int maxLines, int maxLength, bool removeWebLines)
        {
            if (String.IsNullOrWhiteSpace(value)) return String.Empty;
            value = value.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\r", "\n");
            string[] lines = value.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> kept = new List<string>();
            foreach (string raw in lines)
            {
                string line = Regex.Replace(raw.Trim(), @"\s+", " ");
                if (line.Length == 0) continue;
                if (removeWebLines && (line.StartsWith("[网络]", StringComparison.Ordinal) ||
                                       line.StartsWith("[互联网]", StringComparison.Ordinal))) continue;
                kept.Add(line.Replace('\t', ' '));
                if (kept.Count >= maxLines) break;
            }
            string result = String.Join("\n", kept.ToArray());
            if (result.Length > maxLength) result = result.Substring(0, maxLength).TrimEnd() + "…";
            return result;
        }

        private static string CleanExchange(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return String.Empty;
            value = value.Trim().Replace('\t', ' ').Replace("\r", String.Empty).Replace("\n", String.Empty);
            return value.Length > 500 ? value.Substring(0, 500) : value;
        }

        private static string ToBase64(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? String.Empty));
        }
    }
}
