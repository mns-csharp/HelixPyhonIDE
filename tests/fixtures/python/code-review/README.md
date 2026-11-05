# Code review fixture

The same four-module order example twice: once written the way a review would pass it, once with
fourteen defects planted. Both halves must parse — a file that does not parse is recorded as
`ReviewReport.Unparsed` and is never reviewed, which would make either assertion meaningless.

`clean/` is the more important half. It exists so the suite can assert **silence**: a review engine
that finds something in every file is not a review engine, it is noise, and the first thing anyone
does with a noisy one is stop reading it. `CodeReviewFixtureTests.TheCleanFixtureProducesNoFindings`
is the false-positive tripwire for the whole rule library — when a new rule breaks it, the rule is
wrong, not the fixture.

## Planted in `faulty/`

| File | Rule | Defect |
|---|---|---|
| `models.py` | CR-COR-006 | `quantity = quantity` instead of `self.quantity = quantity` |
| `models.py` | CR-DES-007 | `lines` is a class attribute, so every `Order` shares one list |
| `models.py` | CR-COR-007 | `self.total() == 0.1` — exact comparison against a float literal |
| `store.py` | CR-SEC-008 | order id and sku spliced into the SQL text with an f-string |
| `store.py` | CR-ERR-001 | `except sqlite3.DatabaseError: pass` |
| `store.py` | CR-COR-003 | `return order` inside `finally` |
| `store.py` | CR-ERR-005 | `open(path, "w")` with no `with` and no `close()` |
| `service.py` | CR-SEC-004 | `api_token` assigned a literal |
| `service.py` | CR-SEC-002 | `subprocess.run(..., shell=True)` on a concatenated command |
| `service.py` | CR-COR-001 | `order.lines.remove(line)` inside `for line in order.lines` |
| `service.py` | CR-COR-004 | `elif tier == "standard"` twice |
| `service.py` | CR-MNT-003 | `FIXME` marker left in |
| `service.py` | CR-MNT-006 | `breakpoint()` left in |
| `service.py` | CR-PERF-001 | `text += ...` building a string in a loop |
| `test_orders.py` | CR-TST-001 | `test_pricing_service_runs` asserts nothing |
