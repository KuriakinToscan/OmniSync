# Histórico de versões — OmniSync

Mudanças visíveis de cada versão, da mais nova para a mais antiga. Datas = merge na `main` (ou do último commit, para versão ainda em revisão). Detalhes técnicos na [Documentação Técnica](docs/DOCUMENTACAO_TECNICA.md).

## 2.0.2 — 2026-11-08
- Versão exibida no cabeçalho (ex.: `v2.0.2`) e novo comando `--versao`.
- Conexões SFTP são fechadas ao fechar a janela e ao fim da linha de comando (o servidor vê encerramento normal, não queda).
- Limpeza de código sem uso e correção dos avisos de compilação (API obsoleta do Avalonia 12).
- CI: artefatos só em push para a `main`, guardados por 3 dias.
- Documentação: Adiar no manual, decisões guardadas, Voltar ao plano, dica de VPN, nova [Referência](docs/REFERENCIA.md) e este histórico.

## 2.0.1 — 2026-11-08
- SFTP rápido em VPN com latência alta: o envio não espera a confirmação de cada pedaço (8 MB a 100 ms de ida e volta: de 27 s para 0,9 s) e até 4 arquivos novos são copiados ao mesmo tempo no destino SFTP.
- Queda no meio do envio é detectada em menos de 1 s.

## 2.0.0 — 2026-11-07
- Origem ou destino em servidor **SFTP** (`sftp://usuario@servidor/pasta`), com favoritos, senha no cofre do sistema e confirmação da chave do servidor.
- Cada cópia conferida pelo SHA-256 calculado no próprio servidor (ou pelo tamanho, em servidor só-SFTP); opção de reler pela rede.
- Servidor que cai no meio interrompe a execução sem cópia parcial; a próxima continua de onde parou. Códigos de saída 7 e 8 na linha de comando.
- Temporários deixados por execução interrompida são apontados na análise e apagados na sincronização.
- Pastas de rede sem resposta não travam a janela (limite de 5 s); **Abrir origem** e **Abrir destino** na tela de resultado.

## 1.9.1 — 2026-11-02 (versão de teste)
- Camada de arquivos (`IFileSystem`) entre o motor e o disco e primeira versão do SFTP, consolidadas na 2.0.0.

## 1.5.2 — 2026-11-02
- Seletor de horário com relógio no agendamento e no Adiar.

## 1.5.1 — 2026-11-02
- Botão **Adiar** no aviso de execução agendada (15 min, 1 h, 4 h); aviso com Adiar não fecha sozinho.

## 1.5 — 2026-10-31
- Agendamento por tarefa (diário, dias da semana ou ao entrar no sistema), pelo Agendador de Tarefas do Windows ou systemd, sincronizando ou só analisando e avisando.
- Instalação na pasta fixa do usuário, com atalhos; histórico de execuções e verificação do agendamento ao abrir.

## 1.4 — 2026-10-31
- Linha de comando: `--listar`, `--analisar`, `--sincronizar`, com códigos de saída para scripts.
- Janela principal abre maximizada.

## 1.3 — 2026-10-30
- "Pares" passam a se chamar **tarefas**; busca sem acentos; janela **Registro** com filtro e cópia.
- Decisões de conflito mantidas entre análises; **Voltar ao plano**; origem e destino aninhados apontados na hora.
- Cada cópia conferida por SHA-256 antes de ocupar o lugar no destino.
- Erros do sistema em português; CSV com cabeçalho e códigos ASCII.

## 1.2.0 — 2026-10-29
- Nova interface em telas (configuração, plano, execução, resultado) com tarefas salvas na barra lateral.
- Executáveis publicados com as bibliotecas nativas embutidas.

## 1.1 — 2026-10-28
- Integridade de dados (cópia atômica, restauração sem sobrescrever, caixa de nomes no Linux), desempenho em árvores grandes e confirmação antes de sincronizar.
