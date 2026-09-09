; ============================================================================
; Sayl POS - Inno Setup script (v4: zero-question database setup, English UI)
;
; Two installation roles, designed for non-technical users:
;   MAIN     - installs app + (auto-installs free SQL Server Express if no
;              database engine exists), creates SaylDB + demo data silently
;              on fresh installs, opens firewall, fixes TCP port, creates
;              the 'sayl' login used by terminals.
;   TERMINAL - extra cashier PC: connects to the main PC over LAN using
;              just the computer name + access code.
;
; The user never answers any database question: engine detection/install,
; schema creation, seeding and appsettings.json are fully automatic.
;
; Consumes staged output produced by build-installer.ps1:
;   staging\app\*              self-contained publish of Sayl.UI
;   staging\database\xml\*.xml XML envelopes of the SQL scripts
;   staging\redist\SQLEXPR_x64.exe   SQL Server Express 2022 (offline)
; ============================================================================

#ifndef APP_VERSION
  #define APP_VERSION "1.0.0"
#endif

#define MyAppName "Sayl POS"
#define MyAppExeName "Sayl.UI.exe"

[Setup]
AppId={{BCB30A74-8F7A-449D-80B4-EC57C1FBAA22}
AppName={#MyAppName}
AppVersion={#APP_VERSION}
AppPublisher=Sayl
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
; This app installs a machine-wide SQL Server instance, so admin is REQUIRED.
; No "current user" downgrade - otherwise SQL setup (which needs elevation)
; cannot run and C:\ and Program Files writes fail.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
OutputDir=output
OutputBaseFilename=SaylPOS-Setup-{#APP_VERSION}
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\Sayl.UI\Assets\sayl.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "staging\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion createallsubdirs
Source: "staging\database\SaylDB.sql"; DestDir: "{app}\Database"; Flags: ignoreversion
Source: "staging\database\DataSeed.sql"; DestDir: "{app}\Database"; Flags: ignoreversion
Source: "staging\database\xml\*"; DestDir: "{app}\Database\xml"; Flags: ignoreversion
Source: "staging\redist\SQLEXPR_x64_ENU.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

; ============================================================================

[Code]
const
  NL = #13#10;
  SWHIDE = 0;
  SaylLoginName = 'sayl';
  SaylInstance = 'SAYL';
  PwdChars = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';

var
  { ---- role page ---- }
  RolePage: TWizardPage;
  RoleMainRadio: TRadioButton;
  RoleTermRadio: TRadioButton;
  HintMain: TLabel;
  HintTerm: TLabel;

  { ---- terminal page ---- }
  TermPage: TWizardPage;
  TermHostLabel: TLabel;
  TermHostEdit: TEdit;
  TermPassLabel: TLabel;
  TermPassEdit: TEdit;
  TermStatusLabel: TLabel;

  { ---- database page (main role) ---- }
  DbPage: TInputOptionWizardPage;
  DbInfoLabel: TLabel;
  AdvCheck: TCheckBox;
  ServerLabel: TLabel;
  ServerEdit: TEdit;
  WindowsAuthRadio: TRadioButton;
  SqlAuthRadio: TRadioButton;
  SqlUserLabel: TLabel;
  SqlUserEdit: TEdit;
  SqlPassLabel: TLabel;
  SqlPassEdit: TEdit;
  TestButton: TButton;

  { ---- shared state ---- }
  IsMainRole: Boolean;
  StateReady: Boolean;
  FinalServer: String;
  UseSqlAuth: Boolean;
  CustomLogin: Boolean;
  SqlLogin: String;
  SqlPass: String;
  InstanceFound: Boolean;
  DbJustCreated: Boolean;

{ ---------- helpers ---------- }

function ToHex4(V: Integer): String;
begin
  Result :=
    Copy('0123456789ABCDEF', (V shr 12 and 15) + 1, 1) +
    Copy('0123456789ABCDEF', (V shr 8 and 15) + 1, 1) +
    Copy('0123456789ABCDEF', (V shr 4 and 15) + 1, 1) +
    Copy('0123456789ABCDEF', (V and 15) + 1, 1);
end;

function TrimmedText(const S: String): String;
begin
  Result := Trim(S);
  while (Length(Result) > 0) and (Result[Length(Result)] = '\') do
    Delete(Result, Length(Result), 1);
end;

{ Escapes into pure-ASCII JSON body (\uXXXX for anything non-ASCII),
  so the written file stays ANSI-safe while preserving every character. }
function JsonEscape(const S: String): String;
var
  I: Integer;
  C: Char;
begin
  Result := '';
  for I := 1 to Length(S) do
  begin
    C := S[I];
    case Ord(C) of
      $22: Result := Result + '\"';
      $5C: Result := Result + '\\';
      $0A: Result := Result + '\n';
      $0D: Result := Result + '\r';
      $09: Result := Result + '\t';
    else
      if (Ord(C) < $20) or (Ord(C) > $7E) then
        Result := Result + '\u' + ToHex4(Ord(C))
      else
        Result := Result + C;
    end;
  end;
end;

function GenerateAccessCode: String;
var
  G, C, J, Idx: Integer;
  Digits: String;
  D: Char;
begin
  { Pascal Script lacks Randomize; derive pseudo-randomness from the
    current time instead (second-resolution is enough here).
    NOTE: separator args MUST be real chars ('.' ':') - passing '' raises
    a fatal Type Mismatch under some locales/languages. }
  Digits := GetDateTimeString('hhnnsszzz', '.', '.');
  Result := '';
  J := 0;
  for G := 1 to 4 do
  begin
    if G > 1 then
      Result := Result + '-';
    for C := 1 to 4 do
    begin
      J := J + 1;
      D := Digits[(J mod Length(Digits)) + 1];
      Idx := (Ord(D) * 7 + J * 13) mod Length(PwdChars);
      Result := Result + Copy(PwdChars, Idx + 1, 1);
    end;
  end;
end;

function LoadSavedPassword(var Pwd: String): Boolean;
begin
  Result := RegQueryStringValue(HKLM, 'Software\Sayl', 'DbPassword', Pwd);
end;

procedure SavePassword(const Pwd: String);
begin
  RegWriteStringValue(HKLM, 'Software\Sayl', 'DbPassword', Pwd);
end;

function BuildAppConnString: String;
begin
  Result := 'Server=' + FinalServer + ';Database=SaylDB;';
  if UseSqlAuth then
    Result := Result + 'User Id=' + SqlLogin + ';Password=' + SqlPass + ';'
  else
    Result := Result + 'Trusted_Connection=True;';
  Result := Result + 'TrustServerCertificate=True;';
end;

function BuildOleDbConnString(const InitialCatalog: String): String;
begin
  Result := 'Provider=SQLOLEDB;Data Source=' + FinalServer + ';';
  if InitialCatalog <> '' then
    Result := Result + 'Initial Catalog=' + InitialCatalog + ';';
  if UseSqlAuth then
    Result := Result + 'User ID=' + SqlLogin + ';Password=' + SqlPass + ';'
  else
    Result := Result + 'Integrated Security=SSPI;';
end;

function TryOpenConnection(var Conn: Variant; const InitialCatalog: String;
  var ErrMsg: String): Boolean;
begin
  Result := False;
  ErrMsg := '';
  try
    Conn := CreateOleObject('ADODB.Connection');
    Conn.ConnectionTimeout := 2;
    Conn.CommandTimeout := 600;
    Conn.Open(BuildOleDbConnString(InitialCatalog));
    Result := True;
  except
    ErrMsg := GetExceptionMessage;
    Result := False;
  end;
end;

function DatabaseExists(Conn: Variant): Boolean;
var
  RS: Variant;
begin
  Result := False;
  RS := Conn.Execute('SELECT name FROM sys.databases WHERE name = ''SaylDB''');
  Result := not RS.EOF;
  RS.Close;
end;

{ Reads a SQL script from its XML envelope (CDATA) produced by
  build-installer.ps1. MSXML handles the UTF-8 decoding natively - Pascal
  Script has no built-in UTF-8 file reader and no 'raise' statement, so
  failures are reported through the Boolean result. }
function LoadSqlScript(const XmlPath: String; var Content: String): Boolean;
var
  Doc: Variant;
begin
  Result := False;
  Content := '';
  try
    try
      Doc := CreateOleObject('MSXML2.DOMDocument.6.0');
    except
      Doc := CreateOleObject('MSXML2.DOMDocument.3.0');
    end;
    Doc.async := False;
    Doc.validateOnParse := False;
    if Doc.load(XmlPath) then
    begin
      Content := Doc.documentElement.text;
      { Defensive: an envelope may begin with a UTF-8 BOM (U+FEFF) if a
        script was saved with one; SQL Server rejects it as an unknown token.
        Depending on the ANSI/Unicode layering it may also arrive as '?'. }
      if (Length(Content) > 0) and (Ord(Content[1]) = 65279) then
        Content := Copy(Content, 2, Length(Content) - 1);
      while (Length(Content) > 0) and (Copy(Content, 1, 1) = '?') do
        Content := Copy(Content, 2, Length(Content) - 1);
      Result := Content <> '';
    end;
  except
    Result := False;
  end;
end;

{ Executes a T-SQL script splitting on GO batch separators.
  Returns False if the script could not be read; SQL errors surface as
  runtime COM exceptions to the caller. }
function ExecuteGoScript(Conn: Variant; const XmlPath: String; var Batches: Integer): Boolean;
var
  Lines: TStringList;
  Content: String;
  I: Integer;
  Line, Batch: String;
begin
  Result := False;
  Batches := 0;
  if not LoadSqlScript(XmlPath, Content) then
    Exit;
  Lines := TStringList.Create;
  try
    Lines.Text := Content;
    Batch := '';
    for I := 0 to Lines.Count - 1 do
    begin
      Line := Trim(Lines[I]);
      if CompareText(Line, 'GO') = 0 then
      begin
        if Trim(Batch) <> '' then
        begin
          Conn.Execute(Batch);
          Batches := Batches + 1;
        end;
        Batch := '';
      end
      else
        Batch := Batch + Lines[I] + NL;
    end;
    if Trim(Batch) <> '' then
    begin
      Conn.Execute(Batch);
      Batches := Batches + 1;
    end;
    Result := True;
  finally
    Lines.Free;
  end;
end;

procedure AppendLog(Log: TStringList; const Msg: String);
begin
  Log.Add('[' + GetDateTimeString('yyyy/mm/dd hh:nn:ss', '-', ':') + '] ' + Msg);
end;

{ Immediate crash-trace helper: appends straight to disk so we always
  know the last executed step even if the script dies unexpectedly. }
procedure Trace(const Mark: String);
begin
SaveStringToFile(ExpandConstant('{localappdata}\sayl-trace.log'),
    '[' + GetDateTimeString('hhnnsszzz', '.', ':') + '] TRACE ' + Mark + #13#10, True);
end;

{ ---------- SQL Server Express engine automation ---------- }

{ Fix the named instance to TCP port 1433 so other PCs can reach it,
  restart the service, make sure SQL Browser runs and open firewall
  holes. All best effort - failures are logged, never fatal. }
procedure ConfigureForLan(Log: TStringList; const Instance: String);
var
  Names: TArrayOfString;
  I: Integer;
  Svc, TcpKey: String;
  ResCode: Integer;
begin
  if RegGetSubkeyNames(HKLM, 'SOFTWARE\Microsoft\Microsoft SQL Server', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Pos('.' + Uppercase(Instance), Uppercase(Names[I])) > 0 then
      begin
        TcpKey := 'SOFTWARE\Microsoft\Microsoft SQL Server\' + Names[I] +
          '\MSSQLServer\SuperSocketNetLib\Tcp\IPAll';
        RegWriteStringValue(HKLM, TcpKey, 'TcpDynamicPorts', '');
        RegWriteStringValue(HKLM, TcpKey, 'TcpPort', '1433');
        AppendLog(Log, 'Fixed TCP port 1433 (' + Names[I] + ').');
      end;

  Svc := 'MSSQL$' + Instance;
  Exec('net.exe', 'stop "' + Svc + '"', '', SWHIDE, ewWaitUntilTerminated, ResCode);
  Exec('net.exe', 'start "' + Svc + '"', '', SWHIDE, ewWaitUntilTerminated, ResCode);
  AppendLog(Log, 'Service ' + Svc + ' restart code ' + IntToStr(ResCode) + '.');

  Exec('sc.exe', 'config SQLBROWSER start= auto', '', SWHIDE, ewWaitUntilTerminated, ResCode);
  Exec('net.exe', 'start SQLBROWSER', '', SWHIDE, ewWaitUntilTerminated, ResCode);
  AppendLog(Log, 'SQL Browser ensured (code ' + IntToStr(ResCode) + ').');

  Exec('netsh.exe',
    'advfirewall firewall add rule name="SaylPOS SQL TCP" dir=in action=allow protocol=TCP localport=1433',
    '', SWHIDE, ewWaitUntilTerminated, ResCode);
  AppendLog(Log, 'Firewall TCP 1433 rule code ' + IntToStr(ResCode) + '.');
  Exec('netsh.exe',
    'advfirewall firewall add rule name="SaylPOS SQL Browser" dir=in action=allow protocol=UDP localport=1434',
    '', SWHIDE, ewWaitUntilTerminated, ResCode);
  AppendLog(Log, 'Firewall UDP 1434 rule code ' + IntToStr(ResCode) + '.');
end;

{ Returns the last non-empty lines of a text file (error tail). }
function ReadErrorTail(const Path: String; MaxLines: Integer): String;
var
  Lines: TStringList;
  I, Start, Count: Integer;
begin
  Result := '';
  if not FileExists(Path) then
    Exit;
  Lines := TStringList.Create;
  try
    try
      Lines.LoadFromFile(Path);
    except
      Exit;
    end;
    Start := Lines.Count - MaxLines;
    if Start < 0 then Start := 0;
    Count := 0;
    for I := Start to Lines.Count - 1 do
    begin
      if Trim(Lines[I]) <> '' then
      begin
        if Count < MaxLines then
        begin
          Result := Result + Trim(Lines[I]) + NL;
          Count := Count + 1;
        end;
      end;
    end;
  finally
    Lines.Free;
  end;
end;

{ Silently install the bundled SQL Server Express.
  Robust path: unpack the 266 MB bootstrap to a SHORT folder first
  (deep temp paths break the SQL self-extractor / MSI 260-char limit),
  then run the extracted setup.exe. Exit codes 0 AND 3010 (success +
  reboot required) are treated as success - 3010 is NOT a failure. }
function InstallSqlExpress(Log: TStringList): Boolean;
var
  Src, Params, SummaryPath: String;
  ResCode: Integer;
begin
  Result := False;
  Src := ExpandConstant('{tmp}\SQLEXPR_x64_ENU.exe');
  if not FileExists(Src) then
  begin
    AppendLog(Log, 'ERROR: SQLEXPR_x64_ENU.exe was not found inside the installer.');
    Exit;
  end;

  { SQL Server 2022's SQLEXPR_x64_ENU.exe is a self-contained installer that
    IGNORES the old /q /x: extraction switch (it extracts nothing), so we run
    /ACTION=Install directly. The installer always elevates
    (PrivilegesRequired=admin), so the child inherits admin rights. }
  Params := '/Q /ACTION=Install /IACCEPTSQLSERVERLICENSETERMS ' +
    '/SUPPRESSPRIVACYSTATEMENTNOTICE /FEATURES=SQL ' +
    '/INSTANCENAME=' + SaylInstance + ' ' +
    '/SQLSVCSTARTUPTYPE=AUTOMATIC /SQLSYSADMINACCOUNTS="BUILTIN\Administrators" ' +
    '/ADDCURRENTUSERASSQLADMIN=True /SECURITYMODE=SQL /SAPWD="' + SqlPass + '" ' +
    '/TCPENABLED=1 /NPENABLED=0 /UPDATEENABLED=0 /ENU /HIDECONSOLE';
  AppendLog(Log, 'Installing SQL Server Express directly from the bundled installer (5-15 minutes, one time)...');
  WizardForm.StatusLabel.Caption :=
    'Installing the free database engine... please wait (one time only, 5-15 minutes)';
  WizardForm.Repaint;

  if not Exec(Src, Params, '', SWHIDE, ewWaitUntilTerminated, ResCode) then
  begin
    { Last-resort: re-launch so the SQL setup definitely gets admin rights. }
    AppendLog(Log, 'WARN: direct launch failed, retrying elevated...');
    if not ShellExec('runas', Src, Params, '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResCode) then
    begin
      AppendLog(Log, 'ERROR: could not launch SQL Server Express setup.');
      Exit;
    end;
  end;

  { 0 = success; 3010 = success but Windows requests a reboot - BOTH OK }
  if (ResCode <> 0) and (ResCode <> 3010) then
  begin
    AppendLog(Log, 'ERROR: SQL Server Express setup exit code ' + IntToStr(ResCode) + '.');
    SummaryPath := ExpandConstant('{commonpf}\Microsoft SQL Server\160\Setup Bootstrap\Log\Summary.txt');
    if FileExists(SummaryPath) then
      AppendLog(Log, '--- SQL Server Summary.txt (last entries) ---' + NL +
        ReadErrorTail(SummaryPath, 15));
    Exit;
  end;
  if ResCode = 3010 then
    AppendLog(Log, 'SQL Server Express installed OK, but Windows may request a restart.');

  AppendLog(Log, 'SQL Server Express installed OK.');
  Result := True;
end;

{ Create or reset the 'sayl' login through an ADMIN connection. }
procedure EnsureSaylLogin(Log: TStringList; Conn: Variant; const Pwd: String);
var
  RS: Variant;
  Exists: Boolean;
begin
  RS := Conn.Execute('SELECT name FROM sys.sql_logins WHERE name = ''' + SaylLoginName + '''');
  Exists := not RS.EOF;
  RS.Close;
  if Exists then
    Conn.Execute('ALTER LOGIN [' + SaylLoginName + '] WITH PASSWORD=N''' + Pwd + ''', CHECK_POLICY=OFF')
  else
    Conn.Execute('CREATE LOGIN [' + SaylLoginName + '] WITH PASSWORD=N''' + Pwd + ''', CHECK_POLICY=OFF');
  try
    Conn.Execute('ALTER SERVER ROLE sysadmin ADD MEMBER [' + SaylLoginName + ']');
  except
    { probably already a member - harmless }
  end;
  AppendLog(Log, 'Login ''' + SaylLoginName + ''' ready.');
end;

{ ---------- main-role preparation ---------- }

{ Quick Windows-auth connectivity test against one candidate server.
  Temporarily switches global auth state; restores it afterwards. }
function TryProbe(var Server: String): Boolean;
var
  Conn: Variant;
  Err: String;
  SaveAuth: Boolean;
  SaveLogin, SavePwd: String;
  Ok: Boolean;
begin
  SaveAuth := UseSqlAuth; SaveLogin := SqlLogin; SavePwd := SqlPass;
  UseSqlAuth := False;
  Ok := TryOpenConnection(Conn, 'master', Err);
  if Ok then
    Conn.Close;
  UseSqlAuth := SaveAuth; SqlLogin := SaveLogin; SqlPass := SavePwd;
  Result := Ok;
  if not Ok then
    Server := '';
end;

{ Instance keys live under HKLM\SOFTWARE\Microsoft\Microsoft SQL Server.
  Real engines appear as '<VERSION>.<NAME>' (e.g. 'MSSQL17.SAYL',
  'MSSQL17.MSSQLSERVER'). We match on the '.<NAME>' suffix so version
  prefixes pass, while generic keys like 'MSSQLServer' (present on any
  machine with SQL components) never produce a false positive. }
function SqlInstanceKeyExists(const InstanceName: String): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  Suffix: String;
begin
  Result := False;
  Suffix := '.' + Uppercase(InstanceName);
  if RegGetSubkeyNames(HKLM, 'SOFTWARE\Microsoft\Microsoft SQL Server', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Pos(Suffix, Uppercase(Names[I])) > 0 then
      begin
        Result := True;
        Exit;
      end;
end;

{ NOTE: LocalDB is deliberately NOT a candidate. Probing '(localdb)\...' can
  AUTO-START the instance, freezing the wizard for 5-15 seconds on dev PCs
  that happen to have it installed. Shop terminals never use LocalDB - if no
  real engine exists we install our own SQL Express anyway. }

{ Detect an existing local engine or plan a fresh Express install.
  Used by BOTH the interactive wizard and silent installs.
  Registry-gated: we only open a real connection to engines that are
  actually installed, so machines without any SQL Server skip all the
  multi-second network timeouts entirely. }
procedure PrepareMainRole;
var
  SavedPwd: String;
begin
  if StateReady then
    Exit;
  IsMainRole := True;
  SqlLogin := SaylLoginName;
  CustomLogin := False;
  DbJustCreated := False;
  Trace('P0: PrepareMainRole start');

  { show activity while probing - paint this first so the brief checks
    never look like a freeze }
  DbInfoLabel.Caption :=
    'Checking this PC for an existing database engine...';
  WizardForm.Repaint;

  { probe only engines that actually exist on this machine }
  InstanceFound := False;

  if SqlInstanceKeyExists(SaylInstance) then
  begin
    FinalServer := '.\' + SaylInstance;
    Trace('P1: probing .\' + SaylInstance);
    if TryProbe(FinalServer) then
      InstanceFound := True;
  end;

  if (not InstanceFound) and SqlInstanceKeyExists('SQLEXPRESS') then
  begin
    FinalServer := '.\SQLEXPRESS';
    Trace('P2: probing .\SQLEXPRESS');
    if TryProbe(FinalServer) then
      InstanceFound := True;
  end;

  if (not InstanceFound) and SqlInstanceKeyExists('MSSQLSERVER') then
  begin
    FinalServer := '.';
    Trace('P3: probing .');
    if TryProbe(FinalServer) then
      InstanceFound := True;
  end;
  Trace('P4: probes done, found=' + IntToStr(Ord(InstanceFound)));

  if InstanceFound then
  begin
    UseSqlAuth := False;
    DbInfoLabel.Caption :=
      'An existing database engine was found on this PC:' + NL +
      FinalServer + NL +
      'It will be used directly - nothing extra will be installed.';
  end
  else
  begin
    FinalServer := '.\' + SaylInstance;
    UseSqlAuth := True;
    if LoadSavedPassword(SavedPwd) and (SavedPwd <> '') then
      SqlPass := SavedPwd
    else
      SqlPass := GenerateAccessCode();
    DbInfoLabel.Caption :=
      'A free database engine (SQL Server Express) will be installed automatically and silently.' + NL +
      'This step runs once only and may take 5-15 minutes. No internet needed.';
  end;
  StateReady := True;
  Trace('P9: PrepareMainRole done, found=' + IntToStr(Ord(InstanceFound)));
end;

{ Polls the instance until it accepts connections or the timeout passes.
  SQL Server can take a while to become ready after install/restart, so
  we never give up after the first failed probe. }
function WaitForInstanceReady(const Server: String; TimeoutSec: Integer): Boolean;
var
  Tries: Integer;
  ProbeServer: String;
begin
  Result := False;
  Tries := 0;
  while Tries < (TimeoutSec div 5) do
  begin
    ProbeServer := Server;
    if TryProbe(ProbeServer) then
    begin
      Result := True;
      Exit;
    end;
    Tries := Tries + 1;
    if Tries < (TimeoutSec div 5) then
      Sleep(5000);
  end;
end;

{ A previous attempt may have left the SAYL instance installed but its
  service stopped (e.g. interrupted right after setup, or before reboot).
  Try to start it instead of blind-reinstalling over an existing engine. }
procedure TryStartExistingInstance(Log: TStringList);
var
  Svc: String;
  ResCode: Integer;
begin
  if InstanceFound then
    Exit;
  if SqlInstanceKeyExists(SaylInstance) then
  begin
    Svc := 'MSSQL$' + SaylInstance;
    AppendLog(Log, 'Existing instance ' + SaylInstance +
      ' found but not reachable - starting service ' + Svc + ' ...');
    Exec('net.exe', 'start "' + Svc + '"', '', SWHIDE, ewWaitUntilTerminated, ResCode);
    if WaitForInstanceReady(FinalServer, 120) then
    begin
      InstanceFound := True;
      AppendLog(Log, 'Existing instance ' + FinalServer + ' started successfully.');
    end
    else
      AppendLog(Log, 'WARNING: could not start existing instance ' + FinalServer +
        ' - it will be repaired/reinstalled.');
  end;
end;

{ ---------- wizard pages ---------- }

procedure AuthModeChanged(Sender: TObject);
var
  SqlMode: Boolean;
begin
  SqlMode := SqlAuthRadio.Checked;
  SqlUserLabel.Enabled := SqlMode;
  SqlUserEdit.Enabled := SqlMode;
  SqlPassLabel.Enabled := SqlMode;
  SqlPassEdit.Enabled := SqlMode;
end;

procedure AdvancedToggled(Sender: TObject);
var
  On_: Boolean;
begin
  On_ := AdvCheck.Checked;
  ServerLabel.Visible := On_;
  ServerEdit.Visible := On_;
  WindowsAuthRadio.Visible := On_;
  SqlAuthRadio.Visible := On_;
  SqlUserLabel.Visible := On_;
  SqlUserEdit.Visible := On_;
  SqlPassLabel.Visible := On_;
  SqlPassEdit.Visible := On_;
  TestButton.Visible := On_;
  if On_ then
  begin
    ServerEdit.Text := FinalServer;
    if UseSqlAuth then
    begin
      SqlAuthRadio.Checked := True;
      SqlUserEdit.Text := SqlLogin;
      SqlPassEdit.Text := SqlPass;
    end
    else
      WindowsAuthRadio.Checked := True;
    AuthModeChanged(nil);
  end;
end;

procedure TestButtonClick(Sender: TObject);
var
  Conn: Variant;
  ErrMsg: String;
  SaveSrv, SaveLogin, SavePwd: String;
  SaveAuth: Boolean;
  AuthName: String;
begin
  if TrimmedText(ServerEdit.Text) = '' then
  begin
    MsgBox('Please enter a SQL Server name first.', mbCriticalError, MB_OK);
    Exit;
  end;
  SaveSrv := FinalServer; SaveAuth := UseSqlAuth; SaveLogin := SqlLogin; SavePwd := SqlPass;
  FinalServer := TrimmedText(ServerEdit.Text);
  UseSqlAuth := SqlAuthRadio.Checked;
  SqlLogin := Trim(SqlUserEdit.Text);
  SqlPass := SqlPassEdit.Text;
  if TryOpenConnection(Conn, 'master', ErrMsg) then
  begin
    Conn.Close;
    if UseSqlAuth then
      AuthName := 'SQL login'
    else
      AuthName := 'Windows';
    MsgBox('Connection succeeded.' + NL + NL +
      'Server: ' + FinalServer + NL +
      'Authentication: ' + AuthName, mbInformation, MB_OK);
  end
  else
    MsgBox('Connection FAILED:' + NL + NL + ErrMsg, mbCriticalError, MB_OK);
  FinalServer := SaveSrv; UseSqlAuth := SaveAuth; SqlLogin := SaveLogin; SqlPass := SavePwd;
end;

procedure CreateRolePage;
begin
  RolePage := CreateCustomPage(wpWelcome, 'Installation type',
    'How will this computer be used?');

  RoleMainRadio := TRadioButton.Create(RolePage);
  RoleMainRadio.Parent := RolePage.Surface;
  RoleMainRadio.Left := ScaleX(8);
  RoleMainRadio.Top := ScaleY(8);
  RoleMainRadio.Width := ScaleX(430);
  RoleMainRadio.Caption := 'Main computer - install the program and database here';
  RoleMainRadio.Checked := True;

  HintMain := TLabel.Create(RolePage);
  HintMain.Parent := RolePage.Surface;
  HintMain.Left := ScaleX(28);
  HintMain.Top := ScaleY(32);
  HintMain.Width := ScaleX(420);
  HintMain.Height := ScaleY(68);
  HintMain.AutoSize := False;
  HintMain.WordWrap := True;
  HintMain.Font.Color := clGrayText;
  HintMain.Caption :=
    'Choose this for a single-PC shop, or for the first PC of a shop with several terminals. ' +
    'Everything is automatic: the program, the database, and the settings are prepared without any questions.';

  RoleTermRadio := TRadioButton.Create(RolePage);
  RoleTermRadio.Parent := RolePage.Surface;
  RoleTermRadio.Left := ScaleX(8);
  RoleTermRadio.Top := ScaleY(104);
  RoleTermRadio.Width := ScaleX(430);
  RoleTermRadio.Caption := 'Extra terminal (cashier) - connects to the data on the main PC';

  HintTerm := TLabel.Create(RolePage);
  HintTerm.Parent := RolePage.Surface;
  HintTerm.Left := ScaleX(28);
  HintTerm.Top := ScaleY(128);
  HintTerm.Width := ScaleX(420);
  HintTerm.Height := ScaleY(68);
  HintTerm.AutoSize := False;
  HintTerm.WordWrap := True;
  HintTerm.Font.Color := clGrayText;
  HintTerm.Caption :=
    'The main PC must be installed first. You will need its computer name and access code, ' +
    'which were displayed when the main PC setup finished.';
end;

procedure CreateTermPage;
begin
  TermPage := CreateCustomPage(RolePage.ID, 'Connect to the main PC',
    'Enter the details that were shown when the main PC setup finished.');

  TermHostLabel := TLabel.Create(TermPage);
  TermHostLabel.Parent := TermPage.Surface;
  TermHostLabel.Left := ScaleX(0);
  TermHostLabel.Top := ScaleY(8);
  TermHostLabel.Width := ScaleX(430);
  TermHostLabel.Caption := 'Main computer name (e.g. SHOP-PC or 192.168.1.10):';

  TermHostEdit := TEdit.Create(TermPage);
  TermHostEdit.Parent := TermPage.Surface;
  TermHostEdit.Left := ScaleX(0);
  TermHostEdit.Top := ScaleY(28);
  TermHostEdit.Width := ScaleX(260);

  TermPassLabel := TLabel.Create(TermPage);
  TermPassLabel.Parent := TermPage.Surface;
  TermPassLabel.Left := ScaleX(0);
  TermPassLabel.Top := ScaleY(64);
  TermPassLabel.Width := ScaleX(430);
  TermPassLabel.Caption := 'Access Code:';

  TermPassEdit := TEdit.Create(TermPage);
  TermPassEdit.Parent := TermPage.Surface;
  TermPassEdit.Left := ScaleX(0);
  TermPassEdit.Top := ScaleY(84);
  TermPassEdit.Width := ScaleX(260);
  TermPassEdit.PasswordChar := '*';

  TermStatusLabel := TLabel.Create(TermPage);
  TermStatusLabel.Parent := TermPage.Surface;
  TermStatusLabel.Left := ScaleX(0);
  TermStatusLabel.Top := ScaleY(120);
  TermStatusLabel.Width := ScaleX(430);
  TermStatusLabel.Height := ScaleY(56);
  TermStatusLabel.WordWrap := True;
  TermStatusLabel.AutoSize := False;
  TermStatusLabel.Font.Color := clGrayText;
  TermStatusLabel.Caption :=
    'Not sure what to type? Run the installer again on the main PC - the ' +
    'computer name and access code are displayed when it finishes.';
end;

procedure CreateDbPage;
begin
  DbPage := CreateInputOptionPage(RolePage.ID,
    'Database',
    'Everything is automatic - you do not need to change anything.',
    'The database will be set up fully automatically. ' +
    'The options below are for advanced use only.', False, False);

  DbInfoLabel := TLabel.Create(DbPage);
  DbInfoLabel.Parent := DbPage.Surface;
  DbInfoLabel.Left := ScaleX(0);
  DbInfoLabel.Top := ScaleY(4);
  DbInfoLabel.Width := ScaleX(430);
  DbInfoLabel.Height := ScaleY(72);
  DbInfoLabel.WordWrap := True;
  DbInfoLabel.AutoSize := False;
  DbInfoLabel.Caption := '';

  AdvCheck := TCheckBox.Create(DbPage);
  AdvCheck.Parent := DbPage.Surface;
  AdvCheck.Left := ScaleX(0);
  AdvCheck.Top := ScaleY(88);
  AdvCheck.Width := ScaleX(430);
  AdvCheck.Caption :=
    'Advanced: use a specific SQL Server instead of the automatic one';
  AdvCheck.OnClick := @AdvancedToggled;

  { --- advanced area (hidden until AdvCheck is ticked) --- }
  ServerLabel := TLabel.Create(DbPage);
  ServerLabel.Parent := DbPage.Surface;
  ServerLabel.Left := ScaleX(24);
  ServerLabel.Top := ScaleY(116);
  ServerLabel.Caption := 'SQL Server (e.g. . or .\SQLEXPRESS):';
  ServerLabel.Visible := False;

  ServerEdit := TEdit.Create(DbPage);
  ServerEdit.Parent := DbPage.Surface;
  ServerEdit.Left := ScaleX(24);
  ServerEdit.Top := ScaleY(134);
  ServerEdit.Width := ScaleX(220);
  ServerEdit.Visible := False;

  TestButton := TButton.Create(DbPage);
  TestButton.Parent := DbPage.Surface;
  TestButton.Left := ScaleX(252);
  TestButton.Top := ScaleY(132);
  TestButton.Width := ScaleX(110);
  TestButton.Height := ScaleY(24);
  TestButton.Caption := 'Test Connection';
  TestButton.OnClick := @TestButtonClick;
  TestButton.Visible := False;

  WindowsAuthRadio := TRadioButton.Create(DbPage);
  WindowsAuthRadio.Parent := DbPage.Surface;
  WindowsAuthRadio.Left := ScaleX(24);
  WindowsAuthRadio.Top := ScaleY(164);
  WindowsAuthRadio.Width := ScaleX(300);
  WindowsAuthRadio.Caption := 'Windows Authentication';
  WindowsAuthRadio.OnClick := @AuthModeChanged;
  WindowsAuthRadio.Visible := False;

  SqlAuthRadio := TRadioButton.Create(DbPage);
  SqlAuthRadio.Parent := DbPage.Surface;
  SqlAuthRadio.Left := ScaleX(24);
  SqlAuthRadio.Top := ScaleY(184);
  SqlAuthRadio.Width := ScaleX(300);
  SqlAuthRadio.Caption := 'SQL Server authentication';
  SqlAuthRadio.OnClick := @AuthModeChanged;
  SqlAuthRadio.Visible := False;

  SqlUserLabel := TLabel.Create(DbPage);
  SqlUserLabel.Parent := DbPage.Surface;
  SqlUserLabel.Left := ScaleX(48);
  SqlUserLabel.Top := ScaleY(208);
  SqlUserLabel.Caption := 'Login:';
  SqlUserLabel.Visible := False;

  SqlUserEdit := TEdit.Create(DbPage);
  SqlUserEdit.Parent := DbPage.Surface;
  SqlUserEdit.Left := ScaleX(120);
  SqlUserEdit.Top := ScaleY(204);
  SqlUserEdit.Width := ScaleX(180);
  SqlUserEdit.Visible := False;

  SqlPassLabel := TLabel.Create(DbPage);
  SqlPassLabel.Parent := DbPage.Surface;
  SqlPassLabel.Left := ScaleX(48);
  SqlPassLabel.Top := ScaleY(232);
  SqlPassLabel.Caption := 'Password:';
  SqlPassLabel.Visible := False;

  SqlPassEdit := TEdit.Create(DbPage);
  SqlPassEdit.Parent := DbPage.Surface;
  SqlPassEdit.Left := ScaleX(120);
  SqlPassEdit.Top := ScaleY(228);
  SqlPassEdit.Width := ScaleX(180);
  SqlPassEdit.PasswordChar := '*';
  SqlPassEdit.Visible := False;
end;

{ ---------- events ---------- }

procedure InitializeWizard();
begin
  Trace('W0: initwizard enter');
  CreateRolePage;
  Trace('W1: rolepage done');
  CreateTermPage;
  Trace('W2: termpage done');
  CreateDbPage;
  Trace('W3: dbpage done');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = TermPage.ID then
    Result := IsMainRole;
  if PageID = DbPage.ID then
    Result := not IsMainRole;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Conn: Variant;
  ErrMsg: String;
begin
  Result := True;

  { ---- role chosen ---- }
  if CurPageID = RolePage.ID then
  begin
    IsMainRole := RoleMainRadio.Checked;
    if IsMainRole then
      PrepareMainRole
    else
      StateReady := True;   { terminal state is configured on its own page }
    Exit;
  end;

  { ---- terminal page validation ---- }
  if CurPageID = TermPage.ID then
  begin
    if Trim(TermHostEdit.Text) = '' then
    begin
      MsgBox('Please enter the main computer name.', mbCriticalError, MB_OK);
      Result := False;
      Exit;
    end;
    if Trim(TermPassEdit.Text) = '' then
    begin
      MsgBox('Please enter the access code.', mbCriticalError, MB_OK);
      Result := False;
      Exit;
    end;
    TermStatusLabel.Font.Color := clGrayText;
    TermStatusLabel.Caption := 'Checking connection...';

    FinalServer := TrimmedText(TermHostEdit.Text);
    if Pos('\', FinalServer) = 0 then
      FinalServer := FinalServer + '\SAYL';
    UseSqlAuth := True;
    SqlLogin := SaylLoginName;
    SqlPass := Trim(TermPassEdit.Text);
    CustomLogin := False;

    if TryOpenConnection(Conn, 'master', ErrMsg) then
    begin
      Conn.Close;
      TermStatusLabel.Font.Color := clGreen;
      TermStatusLabel.Caption := 'Connected to ' + FinalServer + ' successfully.';
    end
    else
    begin
      TermStatusLabel.Font.Color := clRed;
      TermStatusLabel.Caption := 'Could not connect to the main PC.';
      MsgBox('This PC could not reach the main computer.' + NL + NL +
        'Please check that:' + NL +
        '- The main PC is on and has Sayl POS installed' + NL +
        '- Both PCs are on the same network' + NL +
        '- The name and access code are typed exactly as shown' + NL + NL +
        'Technical details:' + NL + ErrMsg, mbCriticalError, MB_OK);
      Result := False;
    end;
    Exit;
  end;

  { ---- main-role database page ---- }
  if CurPageID = DbPage.ID then
  begin
    if AdvCheck.Checked then
    begin
      if TrimmedText(ServerEdit.Text) = '' then
      begin
        MsgBox('Please enter a SQL Server name.', mbCriticalError, MB_OK);
        Result := False;
        Exit;
      end;
      if SqlAuthRadio.Checked and ((Trim(SqlUserEdit.Text) = '') or (Trim(SqlPassEdit.Text) = '')) then
      begin
        MsgBox('Please enter the SQL login and password.', mbCriticalError, MB_OK);
        Result := False;
        Exit;
      end;
      FinalServer := TrimmedText(ServerEdit.Text);
      UseSqlAuth := SqlAuthRadio.Checked;
      if UseSqlAuth then
      begin
        SqlLogin := Trim(SqlUserEdit.Text);
        SqlPass := SqlPassEdit.Text;
        CustomLogin := CompareText(SqlLogin, SaylLoginName) <> 0;
      end
      else
        CustomLogin := False;
    end;
    Exit;
  end;
end;

procedure WriteAppSettings(const JsonPath: String);
var
  Json: String;
begin
  Json := '{' + NL +
    '  "ConnectionStrings": {' + NL +
    '    "SaylDb": "' + JsonEscape(BuildAppConnString) + '"' + NL +
    '  }' + NL +
    '}';
  SaveStringToFile(JsonPath, Json, False);
end;

procedure DoDatabaseSetup;
var
  Conn: Variant;
  ErrMsg: String;
  Log: TStringList;
  LogPath, DbDir, JsonPath: String;
  Connected, Ok, RunSeed: Boolean;
  N: Integer;
  SeedMsg, ExtraMsg, RoleMsg: String;
  AppAuthMode: Boolean;
begin
  JsonPath := ExpandConstant('{app}\appsettings.json');
  LogPath := ExpandConstant('{app}\install-database.log');
  DbDir := ExpandConstant('{app}\Database');

  Log := TStringList.Create;
  try
    Trace('M0: entered DoDatabaseSetup');

    { silent installs never see the wizard - apply main-role defaults }
    if not StateReady then
      PrepareMainRole;

    Trace('M1: role prepared');

    if IsMainRole then
      RoleMsg := 'main'
    else
      RoleMsg := 'terminal';
    AppendLog(Log, 'Role: ' + RoleMsg + ', server: ' + FinalServer);

    { 1) always write the chosen connection string }
    try
      WriteAppSettings(JsonPath);
      AppendLog(Log, 'Wrote appsettings.json: ' + BuildAppConnString);
      Trace('M2: appsettings written');
    except
      AppendLog(Log, 'ERROR writing appsettings.json: ' + GetExceptionMessage);
    end;

    Ok := False;
    Connected := False;
    RunSeed := False;

    if IsMainRole then
    begin
      { 2a) install the engine if nothing usable was found. Recover a
            stopped-but-existing instance first, then install if needed. }
      if not InstanceFound then
      begin
        TryStartExistingInstance(Log);
        if not InstanceFound then
          if InstallSqlExpress(Log) then
          begin
            SavePassword(SqlPass);
            ConfigureForLan(Log, SaylInstance);
            InstanceFound := True;
          end;
      end;

      if InstanceFound then
      begin
        { give the engine up to 3 minutes to accept connections }
        if not WaitForInstanceReady(FinalServer, 180) then
          AppendLog(Log, 'NOTE: engine is not accepting connections yet.');
        { 2b) ensure the 'sayl' login exists - via WINDOWS admin connection }
        if not CustomLogin then
        begin
          AppAuthMode := UseSqlAuth;
          UseSqlAuth := False;
          if TryOpenConnection(Conn, 'master', ErrMsg) then
          begin
            try
              try
                EnsureSaylLogin(Log, Conn, SqlPass);
              except
                AppendLog(Log, 'NOTE: could not create/reset ''' + SaylLoginName +
                  ''' login (' + GetExceptionMessage + '). Single-PC use still works.');
              end;
            finally
              Conn.Close;
            end;
          end
          else
            AppendLog(Log, 'NOTE: admin connection failed: ' + ErrMsg);
          Trace('M3: admin login block done');
          UseSqlAuth := AppAuthMode;
        end;

        { 2c) reconnect exactly the way the APP will connect }
        Connected := TryOpenConnection(Conn, 'master', ErrMsg);
        Trace('M4: appcred connect done');
        if not Connected then
          AppendLog(Log, 'ERROR connecting with application credentials: ' + ErrMsg);
      end;
    end
    else
    begin
      { terminal: connect with the entered access code }
      Connected := TryOpenConnection(Conn, 'master', ErrMsg);
      if not Connected then
        AppendLog(Log, 'ERROR connecting: ' + ErrMsg);
    end;

    { 3) schema }
    Trace('M5: before schema block');
    if Connected then
    begin
      try
        Ok := True;

        if DatabaseExists(Conn) then
          AppendLog(Log, 'Database SaylDB already exists - keeping it as-is (fresh-install-only system).')
        else
        begin
          AppendLog(Log, 'Creating database SaylDB...');
          try
            if ExecuteGoScript(Conn, DbDir + '\xml\SaylDB.xml', N) then
            begin
              AppendLog(Log, Format('SaylDB.sql OK (%d batches).', [N]));
              DbJustCreated := True;
            end
            else
            begin
              AppendLog(Log, 'ERROR: cannot read SaylDB envelope.');
              Ok := False;
            end;
          except
            AppendLog(Log, 'ERROR creating schema: ' + GetExceptionMessage);
            Ok := False;
          end;
        end;

        { Demo data: loaded automatically ONLY on a brand-new database -
          guarantees a working admin login without asking the user anything.
          Existing databases are always kept as-is. }
        if IsMainRole and DbJustCreated and Ok then
        begin
          try
            if ExecuteGoScript(Conn, DbDir + '\xml\DataSeed.xml', N) then
            begin
              AppendLog(Log, Format('DataSeed.sql OK (%d batches).', [N]));
              RunSeed := True;
            end
            else
            begin
              AppendLog(Log, 'ERROR: cannot read DataSeed envelope.');
              Ok := False;
            end;
          except
            AppendLog(Log, 'ERROR running DataSeed.sql: ' + GetExceptionMessage);
            Ok := False;
          end;
        end
        else if IsMainRole and not DbJustCreated then
          AppendLog(Log, 'Existing database kept as-is (demo data skipped).');
      finally
        Conn.Close;
      end;
    end;

    Log.SaveToFile(LogPath);

    { never block unattended installs with script dialogs }
    if WizardSilent() then
      Exit;

    if not Ok then
      MsgBox('Database setup did not complete successfully.' + NL + NL +
        '--- last log entries ---' + NL + ReadErrorTail(LogPath, 8) + NL +
        'Full details were saved to:' + NL + LogPath + NL + NL +
        'If SQL Server just asked for a Windows restart, restart this PC and run this installer again - ' +
        'it will detect the installed engine and finish the database setup by itself.' + NL + NL +
        'You can also create the database manually later by running the SQL scripts in:' + NL +
        DbDir, mbCriticalError, MB_OK)
    else
    begin
      { shown on the MAIN PC when terminals will ever connect to it }
      ExtraMsg := '';
      if IsMainRole and (not CustomLogin) and UseSqlAuth and (SqlPass <> '') then
        ExtraMsg := NL + '--- For extra cashier terminals ---' + NL +
          'Computer name : ' + GetComputerNameString + NL +
          'Access code   : ' + SqlPass + NL +
          'Write these down - they will be required when installing each extra terminal.';
      { first admin login - only when demo data was just loaded }
      if RunSeed then
        SeedMsg := NL + '--- First admin login ---' + NL +
          'User     : admin' + NL +
          'Password : Admin@123' + NL +
          '(Change the password after the first login)'
      else
        SeedMsg := '';
      MsgBox('Setup finished successfully.' + NL + NL +
        'Connection : ' + FinalServer + NL +
        'Database   : SaylDB' + NL +
        SeedMsg + ExtraMsg + NL + NL +
        'Full log: ' + LogPath, mbInformation, MB_OK);
    end;
  finally
    Log.Free;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    Trace('S0: postinstall reached');
    DoDatabaseSetup;
    Trace('S9: DoDatabaseSetup returned');
  end;
end;


