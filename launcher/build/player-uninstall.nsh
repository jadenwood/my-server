; Realm (player) uninstaller addition, included by build/player.json (nsis.include).
; The app registers the realm:// link handler itself on first start (app.setAsDefaultProtocolClient), which writes
; HKCU\Software\Classes\realm with the command "<install folder>\Realm.exe" "%1". A real uninstall removes that key,
; but only when it still points at this install, so another program's handler is never touched. Updates keep it.
; Settings (%APPDATA%\Realm Player) are kept: nsis.deleteAppDataOnUninstall is false.
!macro customUnInstall
  ${ifNot} ${isUpdated}
    ReadRegStr $0 HKCU "Software\Classes\realm\shell\open\command" ""
    ${if} $0 == '"$INSTDIR\${APP_EXECUTABLE_FILENAME}" "%1"'
      DeleteRegKey HKCU "Software\Classes\realm"
    ${endIf}
  ${endIf}
!macroend
