# Atualizações incrementais

O updater aceita um asset incremental opcional com o formato:

```text
EasySharePatch_from_<versao-atual>_to_<versao-nova>.exe
```

Exemplo:

```text
EasySharePatch_from_1_0_0_21_to_1_0_0_22.exe
```

Esse asset precisa ser um instalador/patch executável completo e assinado, capaz de atualizar somente a versão-base indicada. O updater só o seleciona quando a versão instalada e a versão do release coincidem com o nome do asset; caso contrário, usa `EasyShareSetup.exe`/MSI/MSIX.

Não é permitido aplicar um diff binário diretamente dentro do MSIX instalado: isso quebraria a assinatura do pacote e faria o Windows rejeitar a implantação. A geração do patch deve, portanto, produzir um executável assinado com a mesma cadeia confiável da release e manter o instalador completo como fallback.
