@echo off
setlocal


:: Preserve the original directory path
set "orig_dir=%~dp0"

echo "+--------------------------------------+"
echo "^  <<<<==== Noxy-RED ====>>>>          ^"
echo "^                             by Yeti  ^"
echo "+--------------------------------------+"



:: Check if Voxta.DesktopApp is running

echo
:check_voxta_running
tasklist /FI "IMAGENAME eq Voxta.DesktopApp.exe" 2>NUL | find /I "Voxta.DesktopApp.exe" >NUL
if %errorLevel% neq 0 (
    echo Voxta.DesktopApp is not running. Please start the application manually.
    echo Once started, return to this command window and type Y when ready, or Z to check again.
    
    :ask_voxta_ready
    set /p ready=Have you started Voxta? Type Y to check again: 
    if /I "%ready%"=="Y" (
        goto check_voxta_running
    ) else if /I "%ready%"=="Z" (
        goto check_voxta_running
    ) else (
        echo Invalid input. Please type Y or CTRL + C to terminate.
        goto ask_voxta_ready
    )
) else (
    echo Voxta.DesktopApp is running.
)


:: Check if Docker stack is already running (ensure both Node-RED and MQTT are running)
echo Checking if Docker services (Node-RED and MQTT) are already running...
docker ps | findstr "nodered" >nul 2>&1
set "nodered_running=%errorlevel%"

docker ps | findstr "mqtt" >nul 2>&1
set "mqtt_running=%errorlevel%"

if %nodered_running% neq 0 if %mqtt_running% neq 0 (
    echo Neither Node-RED nor MQTT services are running. Starting Docker stack...
    if exist "%orig_dir%docker-stack\docker-compose.yml" (
        cd "%orig_dir%docker-stack"
        docker-compose up -d
    ) else (
        echo docker-compose.yml not found in %orig_dir%docker-stack directory.
        pause
    )
) else if %nodered_running% neq 0 (
    echo Only MQTT is running. Starting Node-RED...
    cd "%orig_dir%docker-stack"
    docker-compose up -d nodered
) else if %mqtt_running% neq 0 (
    echo Only Node-RED is running. Starting MQTT...
    cd "%orig_dir%docker-stack"
    docker-compose up -d mqtt
) else (
    echo Both Node-RED and MQTT are already running.
)

:: Step 1: Get the local machine IP (ignore localhost and loopback)
for /f "tokens=2 delims=:" %%a in ('ipconfig ^| findstr /C:"IPv4"') do (
    set "local_ip=%%a"
    goto :ip_found
)

:ip_found
set local_ip=%local_ip:~1%
echo Local Machine IP: %local_ip%


:: Start VoxtaMQTTV2.exe
echo.
echo Starting VoxtaMQTTV2.exe...
if exist "%orig_dir%bin\ProviderAPP\VoxtaMQTTV2.exe" (
    start "" "%orig_dir%bin\ProviderAPP\VoxtaMQTTV2.exe"
    echo VoxtaMQTTV2.exe started.
) else (
    echo VoxtaMQTTV2.exe not found in %orig_dir%bin\ProviderAPP.
)

:: Display the local IP address and port information for Node-RED
echo.
echo Node-RED is accessible at: http://%local_ip%:1880
echo.
echo Voxta OSR Integration not started.
echo.
echo The script has completed its tasks, you can close this Window.
exit
