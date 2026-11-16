# Architecture Decision Records

Short records of decisions that shape the codebase: the context, what we chose, and what it
costs. They explain _why_; [architecture.md](../architecture.md) explains _how_.

| ADR                                          | Decision                                         | Status   |
| -------------------------------------------- | ------------------------------------------------ | -------- |
| [0001](0001-dotnet-over-rust-and-go.md)      | .NET over Rust or Go                             | Accepted |
| [0002](0002-sqlite-with-stored-intervals.md) | SQLite with stored intervals, not samples        | Accepted |
| [0003](0003-local-http-api-and-react-ui.md)  | Local HTTP API and React UI instead of native UI | Accepted |
| [0004](0004-github-actions-pipeline.md)      | GitHub Actions as the pipeline                   | Accepted |
| [0005](0005-privacy-gaps-not-intervals.md)   | Paused and excluded time is a gap                | Accepted |
| [0006](0006-velopack-installer-and-updates.md) | Velopack for the installer and updates         | Accepted |

## Writing a new one

Copy the shape of an existing record, give it the next number, and add it to the table.
Keep it to a page. An ADR is not edited once accepted; if a decision changes, write a new
record that supersedes the old one and set the old one's status to
`Superseded by NNNN`.
