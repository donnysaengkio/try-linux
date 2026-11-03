@echo off
rem Builds TryLinux.exe with the C# compiler that ships with Windows.
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REFS=/r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.VisualBasic.dll /r:Microsoft.CSharp.dll /r:System.Core.dll /r:System.Web.Extensions.dll
set SRC=TryLinux.cs Settings.cs

rem First pass builds the icon writer, second pass embeds the icon.
if not exist TryLinux.ico (
  "%CSC%" /nologo /target:winexe /optimize /out:TryLinux.exe %REFS% %SRC% || exit /b 1
  .\TryLinux.exe --write-icon TryLinux.ico || exit /b 1
)
"%CSC%" /nologo /target:winexe /optimize /win32icon:TryLinux.ico /out:TryLinux.exe %REFS% %SRC% || exit /b 1
echo Built TryLinux.exe
