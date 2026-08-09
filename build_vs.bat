@echo off
"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe" "D:\vibercodeing\wslcUI\src\wslcUI\wslcUI.csproj" /p:Configuration=Debug /p:Platform=x64 /v:minimal /restore
echo EXIT=%errorlevel%
