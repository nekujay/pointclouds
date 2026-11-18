@echo off
rem Convert a point cloud to Potree format (octree) for the web viewer.
rem Usage:
rem   convert.cmd "C:\scan\office.las" -Id office-1 -Profile web
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0convert.ps1" %*
