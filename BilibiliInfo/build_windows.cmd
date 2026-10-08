@echo off
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
 echo .NET Framework 4.x compiler was not found.
 echo Please install .NET Framework 4.8 from Microsoft.
 pause
 exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /win32manifest:src\app.manifest /win32icon:src\app.ico /out:BilibiliInfo.exe /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll src\BilibiliInfo.cs
if errorlevel 1 (
 echo Build failed. Close the app before building and check the errors above.
 pause
 exit /b 1
)
echo Build complete: BilibiliInfo.exe
pause
