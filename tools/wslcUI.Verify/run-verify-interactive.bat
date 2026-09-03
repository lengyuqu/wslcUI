@echo off
cd /d D:\vibercodeing\wslcUI
dotnet run --project tools/wslcUI.Verify -p:Platform=x64 -c Debug > %TEMP%\wslcui-verify-out.txt 2>&1
echo done > %TEMP%\wslcui-verify-done.txt
