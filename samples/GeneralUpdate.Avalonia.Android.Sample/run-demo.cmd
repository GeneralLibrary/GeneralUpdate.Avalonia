@echo off
setlocal EnableExtensions EnableDelayedExpansion

if /i not "%~1"=="--run" (
  set "DEMO_LOG=%~dp0run-demo.log"
  echo Starting GeneralUpdate Android auto-update demo...
  echo Full log: !DEMO_LOG!
  echo.
  call "%~f0" --run > "!DEMO_LOG!" 2>&1
  set "DEMO_EXIT_CODE=!ERRORLEVEL!"
  type "!DEMO_LOG!"
  echo.
  if "!DEMO_EXIT_CODE!"=="0" (
    echo Demo preparation completed successfully.
  ) else (
    echo Demo preparation failed with exit code !DEMO_EXIT_CODE!.
    echo Read the first ERROR message above to identify the missing dependency or failed step.
  )
  echo.
  pause
  exit /b !DEMO_EXIT_CODE!
)

set "SAMPLE_DIR=%~dp0"
set "SAMPLE_PROJECT=%SAMPLE_DIR%GeneralUpdate.Avalonia.Android.Sample.csproj"
set "SERVER_PROJECT=%SAMPLE_DIR%DemoServer\GeneralUpdate.DemoServer.csproj"
set "ARTIFACT_DIR=%SAMPLE_DIR%artifacts"
set "APK_V1=%ARTIFACT_DIR%\GeneralUpdate.Sample-v1.apk"
set "APK_V2=%ARTIFACT_DIR%\GeneralUpdate.Sample-v2.apk"
set "PACKAGE_ID=com.generallibrary.generalupdate.avalonia.sample"

echo Script revision: 2026-10-02.3
echo Started at: %DATE% %TIME%
echo [1/8] Checking .NET SDK...
echo Sample directory: %SAMPLE_DIR%
where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: dotnet was not found. Install the .NET 10 SDK first.
  exit /b 1
)

dotnet --list-sdks | findstr /b "10." >nul
if errorlevel 1 (
  echo ERROR: .NET 10 SDK is required.
  dotnet --list-sdks
  exit /b 1
)

echo [2/8] Checking .NET Android workload...
dotnet workload list | findstr /i "android" >nul
if errorlevel 1 (
  echo Android workload is missing. Installing it now...
  dotnet workload install android
  if errorlevel 1 (
    echo ERROR: Failed to install the Android workload.
    exit /b 1
  )
)

echo [3/8] Locating ADB...
set "ADB="
for /f "delims=" %%F in ('where adb 2^>nul') do (
  if not defined ADB set "ADB=%%F"
)

if not defined ADB if defined ANDROID_SDK_ROOT if exist "%ANDROID_SDK_ROOT%\platform-tools\adb.exe" set "ADB=%ANDROID_SDK_ROOT%\platform-tools\adb.exe"
if not defined ADB if defined ANDROID_HOME if exist "%ANDROID_HOME%\platform-tools\adb.exe" set "ADB=%ANDROID_HOME%\platform-tools\adb.exe"

if not defined ADB (
  echo ERROR: adb was not found. Install Android SDK Platform-Tools and add it to PATH.
  exit /b 1
)

"%ADB%" version >nul 2>nul
if errorlevel 1 (
  echo ERROR: adb was not found. Install Android SDK Platform-Tools and add it to PATH.
  exit /b 1
)

where curl.exe >nul 2>nul
if errorlevel 1 (
  echo ERROR: curl.exe is required to verify that the demo server started.
  exit /b 1
)

echo [4/8] Checking connected Android device or emulator...
call :EnsureDevice
if errorlevel 1 exit /b 1

if exist "%ARTIFACT_DIR%" rmdir /s /q "%ARTIFACT_DIR%"
mkdir "%ARTIFACT_DIR%"

echo [5/8] Building the update target APK, version 2.0.0...
dotnet build "%SAMPLE_PROJECT%" -c Debug -t:Rebuild -p:ApplicationDisplayVersion=2.0.0 -p:ApplicationVersion=2 -p:EmbedAssembliesIntoApk=true -p:AndroidUseSharedRuntime=false
if errorlevel 1 exit /b 1
call :CopyBuiltApk "%APK_V2%"
if errorlevel 1 exit /b 1

echo [6/8] Building the installed APK, version 1.0.0...
dotnet build "%SAMPLE_PROJECT%" -c Debug -t:Rebuild -p:ApplicationDisplayVersion=1.0.0 -p:ApplicationVersion=1 -p:EmbedAssembliesIntoApk=true -p:AndroidUseSharedRuntime=false
if errorlevel 1 exit /b 1
call :CopyBuiltApk "%APK_V1%"
if errorlevel 1 exit /b 1

echo [7/8] Building and starting the GeneralSpacestation-compatible demo server...
dotnet build "%SERVER_PROJECT%" -c Release
if errorlevel 1 exit /b 1

start "GeneralUpdate Demo Server" cmd.exe /k dotnet run --project "%SERVER_PROJECT%" -c Release --no-build -- --apk "%APK_V2%"

set "SERVER_READY="
for /l %%N in (1,1,15) do (
  curl.exe --fail --silent "http://127.0.0.1:5080/health" >nul 2>nul
  if not errorlevel 1 (
    set "SERVER_READY=1"
    goto ServerReady
  )
  timeout /t 1 /nobreak >nul
)

:ServerReady
if not defined SERVER_READY (
  echo ERROR: The demo server did not become ready on http://127.0.0.1:5080.
  echo Close any previous "GeneralUpdate Demo Server" window and run this script again.
  exit /b 1
)

"%ADB%" reverse tcp:5080 tcp:5080
if errorlevel 1 (
  echo ERROR: Failed to map device port 5080 to the demo server.
  exit /b 1
)

echo [8/8] Installing and launching version 1.0.0...
"%ADB%" uninstall "%PACKAGE_ID%" >nul 2>nul
"%ADB%" install "%APK_V1%"
if errorlevel 1 exit /b 1

"%ADB%" shell am force-stop "%PACKAGE_ID%"
"%ADB%" shell monkey -p "%PACKAGE_ID%" -c android.intent.category.LAUNCHER 1 >nul

echo.
echo ============================================================
echo Demo is ready.
echo 1. The device is running version 1.0.0.
echo 2. Tap "Check and auto update" in the app.
echo 3. Allow unknown-app installs when Android requests it.
echo 4. Confirm the installation of version 2.0.0.
echo 5. Reopen the app and verify that Current Version is 2.0.0.
echo ============================================================
exit /b 0

:CopyBuiltApk
set "BUILT_APK="
for /f "delims=" %%F in ('where /r "%SAMPLE_DIR%bin\Debug\net10.0-android" *-Signed.apk 2^>nul') do (
  if not defined BUILT_APK set "BUILT_APK=%%F"
)
if not defined BUILT_APK (
  for /f "delims=" %%F in ('where /r "%SAMPLE_DIR%bin\Debug\net10.0-android" *.apk 2^>nul') do (
    if not defined BUILT_APK set "BUILT_APK=%%F"
  )
)
if not defined BUILT_APK (
  echo ERROR: The Android build completed but no APK was found.
  exit /b 1
)
copy /y "!BUILT_APK!" "%~1" >nul
echo APK copied to %~1
exit /b 0

:EnsureDevice
"%ADB%" get-state 2>nul | findstr /x "device" >nul
if not errorlevel 1 exit /b 0

echo No authorized Android device is connected. Trying to start an installed emulator...
"%ADB%" devices

for %%I in ("%ADB%") do set "PLATFORM_TOOLS_DIR=%%~dpI"
for %%I in ("!PLATFORM_TOOLS_DIR!..") do set "ANDROID_SDK_DIR=%%~fI"
set "EMULATOR=!ANDROID_SDK_DIR!\emulator\emulator.exe"

if not exist "!EMULATOR!" (
  echo ERROR: No device is connected and Android Emulator was not found at:
  echo !EMULATOR!
  echo Connect a USB-debugging device or install Android Emulator in Android Studio SDK Manager.
  exit /b 1
)

set "AVD_LIST_FILE=%TEMP%\generalupdate-avds.txt"
"!EMULATOR!" -list-avds > "!AVD_LIST_FILE!" 2>nul
set "AVD_NAME="
set /p AVD_NAME=<"!AVD_LIST_FILE!"
del /q "!AVD_LIST_FILE!" >nul 2>nul

if not defined AVD_NAME (
  echo ERROR: Android Emulator is installed, but no virtual device exists.
  echo Create an Android Virtual Device in Android Studio Device Manager, then rerun this script.
  exit /b 1
)

echo Starting Android emulator: !AVD_NAME!
start "GeneralUpdate Android Emulator" "!EMULATOR!" -avd "!AVD_NAME!"

for /l %%N in (1,1,180) do (
  "%ADB%" get-state 2>nul | findstr /x "device" >nul
  if not errorlevel 1 goto DeviceConnected
  timeout /t 1 /nobreak >nul
)

echo ERROR: The Android emulator did not connect to ADB within 180 seconds.
exit /b 1

:DeviceConnected
echo Waiting for Android to finish booting...
set "BOOT_STATUS_FILE=%TEMP%\generalupdate-boot-status.txt"
for /l %%N in (1,1,180) do (
  "%ADB%" shell getprop sys.boot_completed > "!BOOT_STATUS_FILE!" 2>nul
  set "BOOT_STATUS="
  set /p BOOT_STATUS=<"!BOOT_STATUS_FILE!"
  if "!BOOT_STATUS!"=="1" goto DeviceReady
  timeout /t 1 /nobreak >nul
)

del /q "!BOOT_STATUS_FILE!" >nul 2>nul
echo ERROR: Android did not finish booting within 180 seconds.
exit /b 1

:DeviceReady
del /q "!BOOT_STATUS_FILE!" >nul 2>nul
exit /b 0
