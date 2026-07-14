# Arquitetura do EasyShare

`AppServices` é o composition root e possui os serviços de processo: banco, autenticação, transporte SharePoint, fila, cache offline, unidade WinFsp, atualização, notificações, políticas, saúde e diagnóstico. Páginas e controles consomem esse grafo; não constroem dependências de infraestrutura.

As áreas de transferências, conflitos, saúde e offline vivem em `OperationsCenterControl` e `OperationsCenterViewModel`. `MainPage` permanece como shell de navegação e coordenador de apresentação enquanto as operações de conteúdo, segurança e persistência ficam nos serviços.

O caminho de I/O remoto usa um transporte HTTP compartilhado, streaming, cancelamento, backpressure, concorrência limitada e retry somente quando seguro. A borda WinFsp continua síncrona por exigência do driver; chamadas de UI e trabalhos longos são assíncronos.

Conteúdo pendente e offline usa `EncryptedFileStore`: AES-GCM em chunks autenticados, chave por usuário protegida com DPAPI, escrita atômica e ACL privada. Metadados não contêm conteúdo de arquivo. A fila e o cache possuem cotas independentes.

`AppServices.Dispose` encerra worker, drive, updater e notificações. `MainPage` remove timers e subscriptions no unload, evitando handlers duplicados.
