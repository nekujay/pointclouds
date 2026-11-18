@echo off
rem Publish point cloud: convert -> clouds.json -> git commit/push -> link.
rem Usage:
rem   publish.cmd "C:\scan\office.las" -Title "Office 1 floor"
rem   publish.cmd "C:\scan\office.las" -Title "Office" -Rcp "data/rcp/office.rcp" -Open
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
