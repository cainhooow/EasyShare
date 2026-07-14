# Atualizações incrementais

O canal GitHub do EasyShare baixa exclusivamente patches incrementais. Para aceitar uma atualização, a máquina precisa possuir o pacote MSIX exato da versão-base no cache local:

```text
%LOCALAPPDATA%\EasyShare\Packages\EasyShare_<versao-atual>_x64.msix
```

O asset segue o formato:

```text
EasySharePatch_from_<versao-atual>_to_<versao-nova>.exe
```

O instalador manual armazena o MSIX assinado após uma instalação bem-sucedida. Sem esse cache, o app informa que não há patch compatível e não faz fallback para EXE, MSI ou MSIX completo. O instalador completo permanece disponível apenas na página da release para instalação inicial ou recuperação manual.

O gerador compara o MSIX-base com o MSIX-alvo em blocos, comprime os blocos inalterados como referências e os alterados como dados literais. O executável do patch:

1. valida o hash SHA-256 do pacote-base;
2. reconstrói byte a byte o MSIX-alvo;
3. valida o hash SHA-256 do MSIX reconstruído;
4. executa o mesmo script de instalação.

Como a saída é exatamente o pacote MSIX já assinado, não há alteração arbitrária dentro do pacote nem quebra da assinatura. O patch e o MSIX-alvo continuam precisando ser publicados com assinatura válida.

Para gerar o asset:

```powershell
.\scripts\Generate-EasySharePatch.ps1 `
  -BaseMsixPath dist\payload-exe\EasyShare_1.0.0.21_x64.msix `
  -TargetMsixPath dist\payload-exe\EasyShare_1.0.0.22_x64.msix `
  -OutputPath dist\payload-patch\EasySharePatch_from_1_0_0_21_to_1_0_0_22.bin
```

Depois, publique o executável `EasySharePatch_from_1_0_0_21_to_1_0_0_22.exe`. O updater exige igualdade com o nome canônico, cache da base, URL válida, digest SHA-256 e assinatura confiável. Setup EXE, MSI, MSIX e patches de outra origem/destino nunca são selecionados automaticamente.

Como `/releases/latest` pode saltar versões, cada release deve publicar um patch direto de cada versão-base ainda suportada para a versão mais recente. Clientes sem um patch direto permanecem na versão atual até que o asset compatível seja publicado ou o usuário faça uma recuperação manual.
