using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using Windows.Globalization;
using Windows.Media.Ocr;

namespace SGFloatingTranslator
{
    internal static class SelfTestProgram
    {
        private static int failures;

        [STAThread]
        private static int Main()
        {
            Check("Accept English", TextLogic.IsEnglishInput("Please take the MRT to City Hall."));
            Check("Accept English with numbers", TextLogic.IsEnglishInput("Platform 2 opens at 8:30."));
            Check("Reject Chinese source", !TextLogic.IsEnglishInput("请帮我解释这句话"));
            Check("Reject Chinese-dominant mixed source", !TextLogic.IsEnglishInput("这个 MRT station 在哪里"));
            Check("Normalise smart apostrophe", TextLogic.NormaliseLookupKey("  DON’T  ") == "don't");
            Check("Normalise smart dash", TextLogic.NormaliseLookupKey("EZ–Link") == "ez-link");
            Check("INPUT size", Marshal.SizeOf(typeof(NativeMethods.INPUT)) == (IntPtr.Size == 8 ? 40 : 28));
            Check("Selection limit", TextLogic.MaxInputCharacters == 3000);

            Stopwatch loadTimer = Stopwatch.StartNew();
            OfflineDictionaryTranslator offline = new OfflineDictionaryTranslator();
            loadTimer.Stop();
            Check("Offline core library loaded", offline.EntryCount >= 47000);
            Check("Offline cold load reasonable", loadTimer.Elapsed < TimeSpan.FromSeconds(15));
            TestOfflineExact(offline);
            TestSingaporeOverlay(offline);
            TestTokenBreakdown(offline);
            TestNotFound(offline);
            TestOfflineDeterminism(offline);
            TestEnglishOnly(offline);
            TestMouseOcrUtilities(offline);
            TestSelectionGestureUtilities();

            Check("Gemini fixed HTTPS endpoint", GeminiTranslator.BuildEndpoint() == "https://generativelanguage.googleapis.com/v1beta/interactions");
            TestGeminiRequest();
            TestGeminiResponseParser();
            TestGeminiIncompleteParser();
            TestGeminiMissingFieldParser();
            TestGeminiMalformedParser();
            TestDeepSeekRequest();
            TestDeepSeekResponseParser();

            Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : failures + " TEST(S) FAILED");
            return failures == 0 ? 0 : 1;
        }

        private static void TestOfflineExact(OfflineDictionaryTranslator offline)
        {
            TranslationResult result = offline.Translate("help");
            Check("Offline exact provider", result.Provider == "offline");
            Check("Offline exact Chinese", !String.IsNullOrWhiteSpace(result.Translation));
            Check("Offline English definition", !String.IsNullOrWhiteSpace(result.SimpleEnglish));
        }

        private static void TestSingaporeOverlay(OfflineDictionaryTranslator offline)
        {
            TranslationResult mrt = offline.Translate("MRT");
            Check("Singapore MRT overlay", mrt.Translation.Contains("地铁") && mrt.SingaporeNote.Length > 0);
            TranslationResult hawker = offline.Translate("hawker centre");
            Check("Longest local phrase", hawker.Translation.Contains("熟食中心") && hawker.MatchKind == "exact");
            TranslationResult singlish = offline.Translate("lah");
            Check("Singlish context warning", singlish.Translation.Contains("语气词") && singlish.SingaporeNote.Contains("固定译法"));
        }

        private static void TestTokenBreakdown(OfflineDictionaryTranslator offline)
        {
            TranslationResult result = offline.Translate("Please take the MRT to the hawker centre.");
            Check("Sentence labelled non-machine", result.MatchKind == "token_breakdown" && result.Translation.Contains("非整句机器翻译"));
            Check("Sentence local coverage", result.CoveredWords > 0 && result.TotalWords >= result.CoveredWords);
            Check("Sentence never claims Gemini", result.Provider == "offline" && !result.MeaningZh.Contains("已发送到 Google"));
        }

        private static void TestNotFound(OfflineDictionaryTranslator offline)
        {
            TranslationResult result = offline.Translate("zyxqvnonword");
            Check("Unknown stays offline", result.MatchKind == "not_found" && result.Provider == "offline");
            Check("Unknown does not auto-cloud", result.MeaningZh.Contains("没有自动联网"));
        }

        private static void TestOfflineDeterminism(OfflineDictionaryTranslator offline)
        {
            string first = offline.Translate("thank you").Translation;
            string second = offline.Translate("THANK YOU!").Translation;
            Check("Offline deterministic normalisation", first == second);
        }

        private static void TestEnglishOnly(OfflineDictionaryTranslator offline)
        {
            bool threw = false;
            try { offline.Translate("这是一句中文"); }
            catch (TranslatorException) { threw = true; }
            Check("Offline engine rejects Chinese", threw);
        }

        private static void TestMouseOcrUtilities(OfflineDictionaryTranslator offline)
        {
            Check("OCR exact phrase lookup", offline.HasExactEntry("hawker centre") && !offline.HasExactEntry("zyxqvnonword"));
            Check("OCR word punctuation trim", TranslationMouseController.NormalizeEnglishWord("(centre).") == "centre");
            Check("OCR rejects numeric token", TranslationMouseController.NormalizeEnglishWord("1234") == String.Empty);

            Rectangle leftScreen = new Rectangle(-1920, 0, 1920, 1080);
            Rectangle nearEdge = TranslationMouseController.ComputeCaptureRegion(new Point(-1915, 4), leftScreen);
            Check("OCR capture stays on negative monitor", nearEdge.Left == leftScreen.Left && nearEdge.Top == leftScreen.Top && leftScreen.Contains(nearEdge));
            Rectangle centred = TranslationMouseController.ComputeCaptureRegion(new Point(-960, 540), leftScreen);
            Check("OCR capture dimensions", centred.Width == 1000 && centred.Height == 320 && leftScreen.Contains(centred));

            bool englishOcr = false;
            foreach (Language language in OcrEngine.AvailableRecognizerLanguages)
            {
                if (language != null && language.LanguageTag.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
                {
                    englishOcr = true;
                    break;
                }
            }
            Check("Windows English OCR installed", englishOcr);
        }

        private static void TestSelectionGestureUtilities()
        {
            Rectangle monitor = new Rectangle(0, 0, 1920, 1080);
            Rectangle forward = TranslationMouseController.ComputeSelectionCaptureRegion(
                new Point(100, 100),
                new Point(500, 130),
                monitor);
            Check(
                "AI selection expands forward drag",
                forward == Rectangle.FromLTRB(90, 82, 511, 149));

            Rectangle reverse = TranslationMouseController.ComputeSelectionCaptureRegion(
                new Point(500, 130),
                new Point(100, 100),
                monitor);
            Check("AI selection reverse drag is identical", reverse == forward);

            Rectangle leftMonitor = new Rectangle(-1920, 0, 1920, 1080);
            Rectangle clipped = TranslationMouseController.ComputeSelectionCaptureRegion(
                new Point(-1915, 5),
                new Point(-1815, 20),
                leftMonitor);
            Check(
                "AI selection clips to negative monitor",
                clipped == Rectangle.FromLTRB(-1920, 0, -1804, 39));

            Rectangle tooNarrow = TranslationMouseController.ComputeSelectionCaptureRegion(
                new Point(100, 100),
                new Point(135, 120),
                monitor);
            Check("AI selection rejects short jitter", tooNarrow == Rectangle.Empty);

            Rectangle tooFlat = TranslationMouseController.ComputeSelectionCaptureRegion(
                new Point(100, 100),
                new Point(300, 105),
                monitor);
            Check("AI selection rejects flat jitter", tooFlat == Rectangle.Empty);

            Check(
                "AI selection normalises whitespace and keeps punctuation",
                TranslationMouseController.NormalizeSelectionText(
                    "  Please,\twait\r\nhere!  ") == "Please, wait here!");
            Check(
                "AI selection removes OCR soft characters",
                TranslationMouseController.NormalizeSelectionText(
                    "co\u00ADoperate\0 today.") == "cooperate today.");
            Check(
                "AI selection accepts Latin diacritics",
                TranslationMouseController.NormalizeSelectionText(
                    "Caf\u00E9 closes early.") == "Caf\u00E9 closes early.");
            Check(
                "AI selection rejects punctuation-only OCR",
                TranslationMouseController.NormalizeSelectionText(" -- 123? ") == String.Empty);
            Check(
                "AI selection rejects Chinese OCR",
                TranslationMouseController.NormalizeSelectionText(
                    "\u8BF7\u5728\u8FD9\u91CC\u7B49\u5F85\u3002") == String.Empty);
            Check(
                "AI selection rejects mixed-script OCR",
                TranslationMouseController.NormalizeSelectionText(
                    "Please \u5728\u8FD9\u91CC wait.") == String.Empty);

            Check(
                "Right double-click accepts identical point",
                TranslationMouseController.IsWithinDoubleClickDistance(
                    new Point(400, 300),
                    new Point(400, 300)));
            Check(
                "Right double-click rejects distant point",
                !TranslationMouseController.IsWithinDoubleClickDistance(
                    new Point(400, 300),
                    new Point(1000400, 1000300)));
        }

        private static void TestGeminiRequest()
        {
            using (GeminiTranslator gemini = new GeminiTranslator())
            {
                JavaScriptSerializer json = new JavaScriptSerializer();
                string body = gemini.BuildRequestJson("Please wait here.");
                Dictionary<string, object> root = json.DeserializeObject(body) as Dictionary<string, object>;
                Check("Gemini request model", root != null && Convert.ToString(root["model"]) == gemini.Model);
                Check("Gemini request store false", root != null && root.ContainsKey("store") && root["store"] is bool && !(bool)root["store"]);
                Check("Gemini structured output", root != null && root.ContainsKey("response_format"));
                Check("Gemini request no sampling", !body.Contains("temperature") && !body.Contains("top_p") && !body.Contains("top_k"));
                Check("Gemini request no OpenAI fields", !body.Contains("safety_identifier") && !body.Contains("gpt-"));
            }
        }

        private static void TestGeminiResponseParser()
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            Dictionary<string, object> inner = new Dictionary<string, object>();
            inner["translation_zh"] = "请在这里等候。";
            inner["part_of_speech"] = "sentence";
            inner["explanation_en"] = "Stay in this place until something happens.";
            inner["practical_usage_en"] = "Use this when asking someone not to leave for a short time.";
            inner["practical_usage_zh"] = "短时间请别人不要离开时使用。";
            inner["example_en"] = "Please wait here while I buy the tickets.";
            inner["example_zh"] = "我去买票时，请在这里等候。";
            Dictionary<string, object> part = new Dictionary<string, object>();
            part["type"] = "text";
            part["text"] = json.Serialize(inner);
            Dictionary<string, object> step = new Dictionary<string, object>();
            step["type"] = "model_output";
            step["status"] = "done";
            step["content"] = new object[] { part };
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["status"] = "completed";
            root["steps"] = new object[] { step };

            TranslationResult result = GeminiTranslator.ParseApiResponse(json.Serialize(root), "Please wait here.");
            Check("Gemini parser Chinese", result.Translation == "请在这里等候。");
            Check("Gemini parser explanation", result.SimpleEnglish.StartsWith("Stay", StringComparison.Ordinal));
            Check("Gemini parser part of speech", result.PartOfSpeech == "sentence");
            Check("Gemini parser practical usage", result.PracticalUsageZh.Contains("使用"));
            Check("Gemini parser fixed direction", result.Provider == "gemini" && result.Direction == "en_to_zh");
            Check("Gemini speech source preserved", result.SpeakText == "Please wait here.");
        }

        private static void TestGeminiIncompleteParser()
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["status"] = "incomplete";
            root["steps"] = new object[0];
            bool threw = false;
            try { GeminiTranslator.ParseApiResponse(json.Serialize(root), "test"); }
            catch (TranslatorException) { threw = true; }
            Check("Gemini incomplete rejected", threw);
        }

        private static void TestGeminiMissingFieldParser()
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            Dictionary<string, object> inner = new Dictionary<string, object>();
            inner["translation_zh"] = "测试";
            Dictionary<string, object> part = new Dictionary<string, object>();
            part["type"] = "text";
            part["text"] = json.Serialize(inner);
            Dictionary<string, object> step = new Dictionary<string, object>();
            step["type"] = "model_output";
            step["content"] = new object[] { part };
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["status"] = "completed";
            root["steps"] = new object[] { step };
            bool threw = false;
            try { GeminiTranslator.ParseApiResponse(json.Serialize(root), "test"); }
            catch (TranslatorException) { threw = true; }
            Check("Gemini missing field rejected", threw);
        }

        private static void TestGeminiMalformedParser()
        {
            bool threw = false;
            try { GeminiTranslator.ParseApiResponse("not-json", "test"); }
            catch (TranslatorException) { threw = true; }
            Check("Gemini malformed response rejected", threw);
        }

        private static void TestDeepSeekRequest()
        {
            using (DeepSeekTranslator deepSeek = new DeepSeekTranslator())
            {
                string body = deepSeek.BuildRequestJson("hawker centre");
                Check("DeepSeek fixed HTTPS endpoint", deepSeek.Endpoint == "https://api.deepseek.com/chat/completions");
                Check("DeepSeek current default model", deepSeek.Model == "deepseek-v4-flash" || !String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEEPSEEK_MODEL")));
                Check("DeepSeek JSON output", body.Contains("json_object"));
                Check("DeepSeek non-streaming", body.Contains("\"stream\":false"));
                Check("DeepSeek thinking disabled", body.Contains("\"type\":\"disabled\""));
                Check("DeepSeek usage fields", body.Contains("practical_usage_en") && body.Contains("part_of_speech"));
            }
        }

        private static void TestDeepSeekResponseParser()
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            Dictionary<string, object> resultData = new Dictionary<string, object>();
            resultData["translation_zh"] = "熟食中心";
            resultData["part_of_speech"] = "noun phrase";
            resultData["explanation_en"] = "A food centre with many cooked-food stalls.";
            resultData["practical_usage_en"] = "Use it when arranging where to eat in Singapore.";
            resultData["practical_usage_zh"] = "在新加坡约人吃饭、说明地点时使用。";
            resultData["example_en"] = "Let's meet at the hawker centre after work.";
            resultData["example_zh"] = "下班后我们在熟食中心见。";
            Dictionary<string, object> message = new Dictionary<string, object>();
            message["role"] = "assistant";
            message["content"] = json.Serialize(resultData);
            Dictionary<string, object> choice = new Dictionary<string, object>();
            choice["finish_reason"] = "stop";
            choice["message"] = message;
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["choices"] = new object[] { choice };

            DeepSeekTranslationResult parsed = DeepSeekTranslator.ParseApiResponse(json.Serialize(root), "hawker centre");
            TranslationResult adapted = parsed.ToTranslationResult();
            Check("DeepSeek parser Chinese", parsed.TranslationZh == "熟食中心");
            Check("DeepSeek parser usage", parsed.PracticalUsageEn.Contains("Singapore"));
            Check("DeepSeek adapter provider", adapted.Provider == "deepseek" && adapted.PartOfSpeech == "noun phrase");
            Check("DeepSeek adapter practical fields", adapted.PracticalUsageZh.Contains("新加坡"));
        }

        private static void Check(string name, bool condition)
        {
            if (condition)
            {
                Console.WriteLine("PASS  " + name);
            }
            else
            {
                failures++;
                Console.WriteLine("FAIL  " + name);
            }
        }
    }
}
