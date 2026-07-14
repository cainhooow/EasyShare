# Políticas corporativas do EasyShare

O EasyShare lê políticas JSON com precedência determinística:

1. padrões do produto;
2. preferências salvas pelo usuário;
3. `%LocalAppData%\EasyShare\Policies\policy.json`;
4. `%ProgramData%\EasyShare\Policies\policy.json`.

A política da máquina vence a do usuário. Campos presentes em qualquer camada de política aparecem como **Gerenciado pela organização** e são reaplicados antes de salvar, impedindo alteração pela interface ou por edição direta do banco local. Uma camada inválida é rejeitada inteira, mas não impede a abertura do app.

## Exemplo (schema 1)

```json
{
  "schemaVersion": 1,
  "tenantId": "contoso.onmicrosoft.com",
  "clientId": "11111111-2222-3333-4444-555555555555",
  "allowedTenantIds": ["contoso.onmicrosoft.com"],
  "allowedSharePointHosts": ["*.sharepoint.com"],
  "browserSessionAllowed": true,
  "interactiveSignInAllowed": true,
  "mountPoint": "S:",
  "startWithWindows": true,
  "autoStartVirtualDrive": true,
  "cacheMinutes": 30,
  "offlineCacheLimitMb": 4096,
  "updateChannel": "microsoftStore",
  "automaticUpdatesRequired": true,
  "uploadQueueQuotaBytes": 10737418240,
  "maxUploadPayloadBytes": 2147483648,
  "payloadRetentionDays": 7,
  "supportBundlesAllowed": true,
  "diagnosticRetentionDays": 14,
  "diagnosticMaxFileBytes": 2097152,
  "diagnosticMaxArchiveFiles": 4
}
```

Client secrets, tokens, senhas, cookies e credenciais são proibidos. O app é um public client.

## Origem, implantação e rollback

Distribua a política de máquina por Intune, GPO ou outro canal de TI que grave em `%ProgramData%`. Preserve as ACLs herdadas de `%ProgramData%`: usuários comuns devem ter somente leitura; apenas Administradores/SYSTEM podem substituir o arquivo. O loader rejeita links simbólicos/reparse points, arquivos acima de 256 KiB, JSON ambíguo, propriedades desconhecidas e versões de schema não suportadas. A segurança da origem é, portanto, a ACL administrada pelo canal corporativo; não copie o arquivo para um local gravável pelo usuário.

Para rollback, remova a propriedade desejada ou o arquivo inteiro. Na próxima abertura, o EasyShare volta à preferência do usuário sem apagar rotas, conteúdo SharePoint, fila ou cache. Mantenha `schemaVersion: 1` até que uma migração futura seja documentada.
