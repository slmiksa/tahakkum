@echo off
rem Publishes the database, the keys, and the five functions to the linked Supabase project.
rem Safe to run again: applied database changes are skipped, and functions are replaced.
chcp 65001 >nul
cd /d "%~dp0.."

echo.
echo === 1 of 3: database ===
call npx.cmd supabase db push --remote --yes
if errorlevel 1 goto failed

echo.
echo === 2 of 3: keys ===
call npx.cmd supabase secrets set --env-file secrets/functions.env --yes
if errorlevel 1 goto failed

echo.
echo === 3 of 3: functions ===
call npx.cmd supabase functions deploy activate refresh issue-batch export-batch admin-users --use-api --no-verify-jwt --yes
if errorlevel 1 goto failed

echo.
echo DEPLOY FINISHED
exit /b 0

:failed
echo.
echo DEPLOY FAILED: see the message above.
exit /b 1
