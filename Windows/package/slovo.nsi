Unicode true
RequestExecutionLevel user
Name "Slovo ${VERSION}"
OutFile "${INSTALLER}"
InstallDir "$LOCALAPPDATA\Programs\Slovo"
ShowInstDetails show
SetCompressor /SOLID lzma

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "Slovo"
VIAddVersionKey "CompanyName" "Slovo"
VIAddVersionKey "LegalCopyright" "Slovo"
VIAddVersionKey "FileDescription" "Slovo Setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"

!include "MUI2.nsh"
!include "x64.nsh"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Slovo"
!define RUN_KEY "Software\Microsoft\Windows\CurrentVersion\Run"
!define SLOVO_ICON "${__FILEDIR__}/../src/Slovo.App/Assets/slovo.ico"
!define MUI_ICON "${SLOVO_ICON}"
!define MUI_UNICON "${SLOVO_ICON}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\Slovo.exe"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

!macro RequireSlovoClosed
    System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "Local\Slovo.App.Instance") p .r0'
    ${If} $0 != 0
        System::Call 'kernel32::CloseHandle(p r0)'
        MessageBox MB_OK|MB_ICONEXCLAMATION "Quit Slovo from the notification area and try again." /SD IDOK
        SetErrorLevel 2
        Abort
    ${EndIf}
!macroend

Function .onInit
    ${IfNot} ${RunningX64}
        MessageBox MB_OK|MB_ICONSTOP "Slovo requires 64-bit Windows." /SD IDOK
        SetErrorLevel 1
        Abort
    ${EndIf}
    !insertmacro RequireSlovoClosed
FunctionEnd

Function un.onInit
    !insertmacro RequireSlovoClosed
FunctionEnd

Section "Slovo"
    SetShellVarContext current
    SetRegView 64
    SetOutPath "$INSTDIR"
    SetOverwrite on
    File /r "${PAYLOAD_FILES}"
    WriteUninstaller "$INSTDIR\Uninstall Slovo.exe"
    CreateShortcut "$SMPROGRAMS\Slovo.lnk" "$INSTDIR\Slovo.exe"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "Slovo"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "Slovo"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\Slovo.exe"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall Slovo.exe"'
    WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall Slovo.exe" /S'
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
    SetShellVarContext current
    SetRegView 64
    ReadRegStr $0 HKCU "${RUN_KEY}" "Slovo"
    ${If} $0 == '"$INSTDIR\Slovo.exe" --autostart'
        DeleteRegValue HKCU "${RUN_KEY}" "Slovo"
    ${EndIf}
    DeleteRegKey HKCU "${UNINSTALL_KEY}"
    Delete "$SMPROGRAMS\Slovo.lnk"
    !include "${UNINSTALL_FILES}"
    Delete "$INSTDIR\Uninstall Slovo.exe"
    RMDir "$INSTDIR"
SectionEnd
