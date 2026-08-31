@echo off
chcp 65001 >nul
title ClearC 编译脚本  (c) zsqstudio

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [失败] 未找到系统 C# 编译器 csc.exe
  pause
  exit /b 1
)

echo 正在使用系统 csc 编译 ClearC.cs ...
"%CSC%" /nologo /target:winexe /platform:anycpu /codepage:65001 /out:ClearC.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll ^
  /r:System.Windows.Forms.dll /r:Microsoft.VisualBasic.dll ClearC.cs

if errorlevel 1 (
  echo.
  echo [失败] 编译出错, 请检查上方错误信息
) else (
  echo.
  echo [成功] 已生成 ClearC.exe  ——  ClearC v1.1  (c) zsqstudio  11016795@qq.com
)
pause
