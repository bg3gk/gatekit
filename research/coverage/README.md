# The coverage record

The record of the earlier way of covering the game: the findings (`findings.jsonl`), the replays,
sightings, wishes and counts (`record/`), where each type was seen alive (`seen/`), the leads
(`leads.jsonl`), the systems' descriptions (`systems/`), what Script Extender declares
(`coverage.json`), and the recipes that replay a finding (`tests/_coverage/`).

It is frozen: kept for reading, and nothing new is written into it. A new finding goes into the
study of its mechanic, under [../studies/](../studies/); a system of findings that replaces this
record is to be designed. Our reading of the game's code - functions, types, their fields and
names - is the registry, [registry/README.md](../../registry/README.md).

`gatekit record check` says what in the record does not hold together; `gatekit record tally`
gives its counts.
