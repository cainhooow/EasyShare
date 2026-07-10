[Version]
Class=IEXPRESS
SEDVersion=3

[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=EasyShare instalado.
TargetName=E:\Projetos\EasyShare\dist\EasyShareSetup.exe
FriendlyName=EasyShare Setup
AppLaunched=powershell.exe -NoProfile -ExecutionPolicy Bypass -File Install-EasyShare.ps1
PostInstallCmd=<None>
AdminQuietInstCmd=powershell.exe -NoProfile -ExecutionPolicy Bypass -File Install-EasyShare.ps1 -NoLaunch
UserQuietInstCmd=powershell.exe -NoProfile -ExecutionPolicy Bypass -File Install-EasyShare.ps1 -NoLaunch
SourceFiles=SourceFiles

[SourceFiles]
SourceFiles0=E:\Projetos\EasyShare\dist\payload-exe\

[SourceFiles0]
%FILE0%=Install-EasyShare.ps1
%FILE1%=EasyShare_1.0.0.22_x64.msix
%FILE2%=EasyShare_TestCertificate.cer
%FILE3%=Microsoft.WindowsAppRuntime.2.msix
%FILE4%=winfsp-2.1.25156.msi

[Strings]
FILE0="Install-EasyShare.ps1"
FILE1="EasyShare_1.0.0.22_x64.msix"
FILE2="EasyShare_TestCertificate.cer"
FILE3="Microsoft.WindowsAppRuntime.2.msix"
FILE4="winfsp-2.1.25156.msi"
