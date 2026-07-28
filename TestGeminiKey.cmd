@echo off
setlocal
title Gemini key tester
echo ============================================================
echo  Gemini key tester  (uses Windows built-in curl)
echo  HTTP=200 or 400  means the key is VALID  (auth passed)
echo  HTTP=401 or 403  means the key was REJECTED by Google
echo ============================================================
set /p KEY=Paste your Gemini API key, then press Enter:
if "%KEY%"=="" goto end
set URL=https://generativelanguage.googleapis.com/v1beta/interactions

echo.
echo --- Test 1: x-goog-api-key header (what Luma uses) ---
curl -sS -m 30 -X POST "%URL%" -H "Content-Type: application/json" -H "x-goog-api-key: %KEY%" -d "{\"model\":\"gemini-3.5-flash-lite\",\"input\":\"Say OK\",\"store\":false}" -w "\nHTTP=%%{http_code}\n"

echo.
echo --- Test 2: key as query parameter ---
curl -sS -m 30 -X POST "%URL%?key=%KEY%" -H "Content-Type: application/json" -d "{\"model\":\"gemini-3.5-flash-lite\",\"input\":\"Say OK\",\"store\":false}" -w "\nHTTP=%%{http_code}\n"

echo.
echo --- Test 3: Authorization Bearer header ---
curl -sS -m 30 -X POST "%URL%" -H "Content-Type: application/json" -H "Authorization: Bearer %KEY%" -d "{\"model\":\"gemini-3.5-flash-lite\",\"input\":\"Say OK\",\"store\":false}" -w "\nHTTP=%%{http_code}\n"

echo.
echo Done. If the key was rejected, screenshot this window and open an issue on GitHub.

:end
pause
exit /b
