@echo off
rem コンソール付きで起動する（エラーメッセージを確認したいとき用）
setlocal
cd /d "%~dp0"

if exist ".venv\Scripts\python.exe" (
    ".venv\Scripts\python.exe" -m app
) else (
    python -m app
)
pause
endlocal
