@echo off
echo Starting Conductor Development Environment...

REM Start Backend API
echo.
echo Starting Backend API...
start "Conductor API" cmd /k "cd /d %~dp0\Conductor && dotnet run"

REM Wait a moment for backend to start
timeout /t 3 /nobreak > nul

REM Start Frontend Dashboard
echo.
echo Starting Frontend Dashboard...
start "Conductor Dashboard" cmd /k "cd /d %~dp0\..\conductor-dash && npm run dev"

REM Wait a moment for frontend to start
timeout /t 3 /nobreak > nul

REM Open demo page
echo.
echo Opening demo page...
start "Demo Site" "%~dp0\Panel\demo.html"

echo.
echo Development environment started!
echo.
echo Services:
echo - Backend API: https://localhost:7215
echo - Dashboard: http://localhost:3000
echo - Demo Site: file://%~dp0\Panel\demo.html
echo.
echo Test Login (Development Only):
echo - Username: admin
echo - Password: admin
echo.
echo Or register new users at: http://localhost:3000/register
echo - Use registration key: change-this-registration-key-in-production
echo.
pause
