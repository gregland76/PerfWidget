@echo off
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /win32manifest:app.manifest /out:PerfWidget.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll PerfWidget.cs
if %errorlevel% equ 0 (
    echo Build OK : PerfWidget.exe
) else (
    echo Build FAILED
)
