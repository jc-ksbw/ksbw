# Election Race Setup

A small Windows app that reads a CSV of races and candidates and sends them to
**Election Manager** over the custom-wire socket described in the *AP_Custom_Wire*
sample, so the races can be set up without typing them in by hand.

It sends each race with **0 votes, 0 precincts reporting** - this is race *setup*,
not results. (Results can be sent later with the same wire format; see "Next steps".)

## Build

Needs the .NET Framework 4.8 targeting pack (already present if you built the
AP_Custom_Wire sample) and either Visual Studio 2019+ or the .NET SDK.

    cd ElectionRaceSetup
    dotnet build -c Release          # or open ElectionRaceSetup.vbproj in Visual Studio

The program is `bin\Release\net48\ElectionRaceSetup.exe` - one exe plus its `.config`.
It matches the sample: VB.NET, WinForms, .NET Framework 4.8.

## Use

1. In Election Manager, **create the election** (File > Create Election) for the date you
   will send. The wire's `ELECTION` date has to match an existing election.
2. Start the app. **Host** defaults to `HATV-MOS` (the sample's value; use the name of
   your MOS server) and **Port** to `10387`. Click **Connect**.
3. Set the election **Date**, and a **Source** name (default `CSV`). Keep the Source the
   same every time: Election Manager identifies a wire race by Election + Source + Race ID,
   so re-sending updates the race instead of duplicating it.
4. **Open CSV...**. Races appear in the grid; click one to see its candidates.
   Red rows have errors (e.g. no candidates) and cannot be sent; yellow rows have warnings.
5. **Preview XML...** shows exactly what will be sent for the selected race.
6. **Send N races**. Progress shows in the status bar and log.
7. In Election Manager, open **Wire Manager** - the races arrive as new wire entries - and
   use **Import From Wire** / **Add Race From Wire** to create them.

**TEST flag.** "Send as TEST races" is on by default (the sample also sends `TEST=YES`).
Untick it for real races; the confirmation dialog tells you which mode you are in.

## CSV format

One row per candidate. Rows with the same **Race ID** become one race; if there is no Race
ID column, rows with identical race columns are grouped and IDs `R001`, `R002`... are
generated (in file order, so keep the order stable between sends).

Columns are matched by header name, ignoring case/spaces/punctuation. If the required ones
can't be found, a mapping dialog opens; **Map columns...** reopens it any time.

| Field | Recognised headers (first match wins) | Notes |
|---|---|---|
| Race ID | Race ID, Contest ID, ID | Stable unique ID per race |
| **Office** (required) | Office, Race, Race Name, Contest, Title, Position | |
| Level | Level | NATIONAL / STATE / COUNTY / CITY / DISTRICT (federal, statewide, municipal, local... are translated). Inferred from county/city/district if blank |
| State | State, St, State Code | Full names become postal codes |
| County / City / District | County, City, District | |
| Race party | Race Party, Primary Party | Primaries only |
| Type | Race Type, Type | e.g. PRIMARY, PROPOSITION, AMENDMENT, QUESTION |
| Seat name / number | Seat Name, Seat, Seat Number | |
| Winners | Winners, Vote For, Seats | Default 1 |
| Total precincts | Total Precincts, Precincts | Default 0 |
| **Candidate** (required) | Candidate, Candidate Name, Full Name, Name - *or* First/Middle/Last/Suffix | Last name is enough if full name is absent. Yes/No ballot measures: use `Yes` and `No` as candidates |
| Candidate party | Party, Candidate Party | |
| Incumbent | Incumbent | Y / Yes / True / 1 |

A working example is in `sample/sample_races.csv`. Files saved from Excel (Windows-1252) or
UTF-8, with comma, semicolon or tab delimiters, are all read.

## Protocol (from the AP_Custom_Wire sample)

* TCP to `<MOS host>:10387`, UTF-8, one message per `CRLF`.
* Message: `$(ELECTION:CLIENT:UPDATE)=<now>=WIREDATA=<RACE>...</RACE>` followed by `CRLF CRLF`
  (as `SendToServer(What & vbCrLf)` does with a payload that already ends in `vbCrLf`).
* The `<RACE>` layout is identical to `SendRace()` in the sample, with XML escaping added.
* A `CRLF` heartbeat goes out every 30 s when idle, and the app reconnects if the
  connection drops (messages queued meanwhile are sent after reconnecting).
* A short pause (default 500 ms, as in the sample) follows each race.

## Tests

The CSV/import/XML/socket logic has no UI dependency and is covered by a console test
project (needs the .NET 8 SDK):

    cd tests/ElectionRaceSetup.Tests && dotnet run

## Next steps

The same `WireBuilder` can carry results: fill `<VOTES>`, `PRECINCTS_REPORTING`, `<WINNER>`
and sub-races per the sample. That is deliberately not built yet.
