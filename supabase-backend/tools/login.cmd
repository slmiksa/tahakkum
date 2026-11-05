@echo off
rem Signs in to Supabase and links this folder to the project. Passwords are typed here only.
chcp 65001 >nul
cd /d "%~dp0.."
echo.
echo  Step 1 of 2: press Enter below, then sign in with the browser that opens.
echo.
call npx.cmd supabase login
echo.
echo  Step 2 of 2: linking the project. If asked, type the database password and press Enter.
echo  The password does not appear while you type.
echo.
call npx.cmd supabase link --project-ref hosmafqkcmcmdpwhzodu
echo.
echo  Finished. You can close this window and tell Claude.
echo.
pause
