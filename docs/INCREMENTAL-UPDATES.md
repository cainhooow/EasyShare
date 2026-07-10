# Atualizações incrementais

O EasyShare usa patches apenas quando a máquina possui o pacote MSIX da versão-base no cache local:

```text
%LOCALAPPDATA%\EasyShare\Packages\EasyShare_<versao-atual>_x64.msix
```

O asset segue o formato:

```text
EasySharePatch_from_<versao-atual>_to_<versao-nova>.exe
```

O instalador normal armazena o MSIX assinado após uma instalação bem-sucedida. Em máquinas antigas sem esse cache, o updater seleciona automaticamente o instalador completo.

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

Depois, publique o executável `EasySharePatch_from_1_0_0_21_to_1_0_0_22.exe` junto com o instalador completo da mesma release. O updater só escolhe o patch quando o nome identifica exatamente a versão instalada e a versão do release.
