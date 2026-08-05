@echo off
setlocal enabledelayedexpansion

rem  Publishes a release of ED8Editor.
rem
rem      publish-release.bat 0.4.1 "What changed"
rem
rem  Builds, packs, tags and publishes in one go, because a release that takes
rem  several steps is a release nobody makes often. The point of small frequent
rem  releases is that publishing one costs nothing.
rem
rem  Needs the GitHub CLI (https://cli.github.com) and "gh auth login" done once.

if "%~1"=="" (
  echo Usage: publish-release.bat ^<version^> ["release notes"]
  echo    e.g. publish-release.bat 0.4.1 "dev/ assets are used; undo covers pasting"
  exit /b 1
)

set "VERSION=%~1"
set "NOTES=%~2"
set "ROOT=%~dp0"
set "STAGE=%ROOT%artifacts\%VERSION%"
set "ZIP=%ROOT%artifacts\ED8Editor-%VERSION%.zip"

rem  Nothing is published from a dirty tree: the build would not match the tag,
rem  and the tag is what people report bugs against.
git -C "%ROOT%" diff --quiet
if errorlevel 1 (
  echo Uncommitted changes. Commit or stash them first.
  exit /b 1
)
git -C "%ROOT%" diff --cached --quiet
if errorlevel 1 (
  echo Staged but uncommitted changes. Commit them first.
  exit /b 1
)

where gh >nul 2>&1
if errorlevel 1 (
  echo The GitHub CLI is not on PATH. See https://cli.github.com
  exit /b 1
)

rem  The version is written into the project as well as passed to the build.
rem
rem  Passing it alone would be enough for the release, but then a build made from
rem  the sources afterwards would still report the old number and be offered an
rem  update to what it already contains. Recording it means the repository always
rem  states which release it is, which is also what someone reading it expects.
echo == Setting the version to %VERSION%
powershell -NoProfile -Command ^
  "$p='%ROOT%src\ED8Editor.Viewer\ED8Editor.Viewer.csproj';" ^
  "$t=Get-Content -Raw $p;" ^
  "$n=[regex]::Replace($t,'<Version>[^<]*</Version>','<Version>%VERSION%</Version>');" ^
  "if($n -ne $t){Set-Content -NoNewline -Encoding utf8 $p $n}"
if errorlevel 1 (
  echo Could not write the version into the project.
  exit /b 1
)
git -C "%ROOT%" diff --quiet
if errorlevel 1 (
  git -C "%ROOT%" add "src/ED8Editor.Viewer/ED8Editor.Viewer.csproj"
  git -C "%ROOT%" commit -q -m "v%VERSION%"
  echo    recorded as a commit
)

echo == Building %VERSION%
dotnet build "%ROOT%ED8Editor.sln" -c Release -p:Version=%VERSION% --nologo -v q
if errorlevel 1 (
  echo The build failed. Nothing was published.
  exit /b 1
)

echo == Running the tests
dotnet run --project "%ROOT%tests\ED8Editor.Tests" -c Release --nologo -v q > "%TEMP%\ed8-tests.txt"
if errorlevel 1 (
  echo Tests failed. Nothing was published.
  type "%TEMP%\ed8-tests.txt" | findstr /b FAIL
  exit /b 1
)

echo == Packing
if exist "%STAGE%" rmdir /s /q "%STAGE%"
mkdir "%STAGE%" 2>nul
dotnet publish "%ROOT%src\ED8Editor.Viewer" -c Release -p:Version=%VERSION% ^
  --nologo -v q -o "%STAGE%"
if errorlevel 1 (
  echo Publishing failed. Nothing was released.
  exit /b 1
)
if exist "%ZIP%" del "%ZIP%"
powershell -NoProfile -Command ^
  "Compress-Archive -Path '%STAGE%\*' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 (
  echo Could not build the archive.
  exit /b 1
)

rem  The notes are what the updater shows to everyone who is offered this build,
rem  so an empty message is filled with the commits since the last tag rather
rem  than left blank.
if "%NOTES%"=="" (
  echo == No notes given, using the commits since the last release
  for /f "delims=" %%t in ('git -C "%ROOT%" describe --tags --abbrev^=0 2^>nul') do set "PREVIOUS=%%t"
  if defined PREVIOUS (
    git -C "%ROOT%" log --pretty=format:"- %%s" !PREVIOUS!..HEAD > "%TEMP%\ed8-notes.txt"
  ) else (
    git -C "%ROOT%" log --pretty=format:"- %%s" -20 > "%TEMP%\ed8-notes.txt"
  )
) else (
  echo %NOTES%> "%TEMP%\ed8-notes.txt"
)

echo == Tagging v%VERSION%
git -C "%ROOT%" tag -a "v%VERSION%" -m "v%VERSION%"
if errorlevel 1 (
  echo That tag already exists. Pick another version.
  exit /b 1
)
rem  The commit first, then the tag: a tag pushed alone would point at a commit
rem  the remote does not have.
git -C "%ROOT%" push origin HEAD
if errorlevel 1 (
  echo Could not push the branch; the release was not created.
  git -C "%ROOT%" tag -d "v%VERSION%" >nul
  exit /b 1
)
git -C "%ROOT%" push origin "v%VERSION%"
if errorlevel 1 (
  echo Could not push the tag; the release was not created.
  git -C "%ROOT%" tag -d "v%VERSION%" >nul
  exit /b 1
)

echo == Publishing
gh release create "v%VERSION%" "%ZIP%" ^
  --repo TwnKey/ED8Editor ^
  --title "v%VERSION%" ^
  --notes-file "%TEMP%\ed8-notes.txt"
if errorlevel 1 (
  echo The release was not created. The tag is pushed: delete it or retry.
  exit /b 1
)

echo.
echo Released v%VERSION%  ^-^-  %ZIP%
echo The editor will offer it to everyone running an older build.
endlocal
