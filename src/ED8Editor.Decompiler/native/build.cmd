@echo off
REM ---------------------------------------------------------------------------
REM Construit cs1_decompiler.dll (x64) a partir des sources natives validees.
REM Prerequis : "Build Tools for Visual Studio" (workload C++). Lancer ce script
REM depuis un "x64 Native Tools Command Prompt for VS" (cl.exe dans le PATH).
REM ---------------------------------------------------------------------------
cd /d "%~dp0"
where cl >nul 2>nul
if errorlevel 1 (
  echo [ERREUR] cl.exe introuvable. Ouvre "x64 Native Tools Command Prompt for VS" puis relance.
  exit /b 1
)
cl /nologo /std:c++17 /O2 /EHsc /D_CRT_SECURE_NO_WARNINGS /LD cs1_instr_api.cpp /Fe:cs1_decompiler.dll
if errorlevel 1 (
  echo [ERREUR] compilation echouee.
  exit /b 1
)
del *.obj 2>nul
del cs1_decompiler.exp 2>nul
del cs1_decompiler.lib 2>nul
echo.
echo OK -> cs1_decompiler.dll
