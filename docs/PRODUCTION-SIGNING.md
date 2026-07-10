# Assinatura de produção

Os artefatos distribuídos precisam ser assinados com um certificado de code signing comercial. O certificado local `CN=AppPublisher` é exclusivo para QA e não deve ser usado em releases para clientes.

## Preparação

1. Instale o certificado comercial com chave privada em `CurrentUser\My` ou `LocalMachine\My`.
2. Configure o thumbprint sem espaços:

```powershell
$env:EASYSHARE_SIGNING_CERT_THUMBPRINT = "THUMBPRINT_DO_CERTIFICADO"
$env:EASYSHARE_SIGNING_TIMESTAMP_URL = "https://SEU-TSR/timestamp"
```

3. Gere os artefatos EXE, MSI e MSIX da mesma versão.
4. Assine e valide:

```powershell
.\scripts\Sign-EasyShareArtifacts.ps1 `
  -MsixPath dist\payload-exe\EasyShare_1.0.0.22_x64.msix `
  -ExePath dist\EasyShareSetup.exe `
  -MsiPath dist\EasyShareSetup.msi `
  -RequireTrustedSignature
```

O script exige assinatura Authenticode válida em todos os artefatos e, com `-RequireTrustedSignature`, exige que `signtool verify /pa` também passe. A publicação deve ser feita somente depois dessa validação.

## Regras

- Nunca inclua a chave privada no repositório, nos assets da release ou no instalador.
- Use timestamp RFC 3161 para manter a assinatura válida após a expiração do certificado.
- O pacote MSIX, o bootstrapper EXE e o MSI devem usar a mesma cadeia confiável.
- O certificado de teste não deve ser importado em `Root`/`TrustedPeople` em uma instalação de produção.
