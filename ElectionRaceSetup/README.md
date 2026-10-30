# Election Race Setup

A small Windows app that reads a CSV of races and candidates and sends them to
**Election Manager** over the custom-wire socket described in the *AP_Custom_Wire*
sample, so the races can be set up without typing them in by hand.

Race setup sends each race with **0 votes, 0 precincts reporting**. Results come from a feed in the
same app. If that feed is the **Election Results Monitor API**, the app can also build the races
themselves from its ballot definition, and then pair results by ID rather than by wording - see
"Election Results Monitor API" below.

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
| Feed ID (optional) | Feed ID, External ID | The race's ID in your results feed; makes pairing exact |
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

## Election Results Monitor API (the quickest path)

When the feed address is the Election Results Monitor API (schema 2), the app uses its IDs instead
of matching on wording, and nothing has to be paired by hand.

**Set the races up from the ballot definition.** In the main window click **From API...**, give it
the address (`http://127.0.0.1:8877`, or its `/api/contests`), and tick the contests you want. Each
race is created with:

| Race field | From the API |
| --- | --- |
| Race ID and Feed ID | `contest_id`, e.g. `ca-governor` - stable and shared across counties |
| Office | `office`, or `Proposition 1` / `Measure E` from `measure_letter` |
| Level | `jurisdiction_type` and `level` → NATIONAL, STATE, COUNTY, CITY or DISTRICT |
| County / City / District | `jurisdiction`, plus `jurisdiction_counties` for a local race |
| Seat name / number | `seat_type` and `seat_number`; the judge's name for a retention |
| Winners | `vote_for` (1 when the API reports none) |
| Type | PROPOSITION, MEASURE or RETENTION |
| Candidates | `ballot_name`, `first`, `middle`, `last`, `suffix`, `party`, each keeping its candidate ID |

Send those races, import them in Wire Manager once, and the setup is done. **Save as CSV...** keeps a
copy (including the IDs) so the same setup can be reloaded without the API.

**Then results pair exactly.** Open **Live results...**, point it at the same address and click
**Fetch now**. The app reads `/api/results`, matches every contest by `contest_id` and every
candidate by `candidate_id`, adds the counties together, and sends each race once. The line above
the grid shows each county's status and the ballot version.

**What the app sends to Election Manager** is only what its wire format can read: race ID, winners,
level, state, county, city, district, office, type, seat, precincts reporting and total, and each
candidate's name, party and votes (plus the county sub-races when that box is ticked). Everything
else the API publishes - percentages, under and over votes, turnout, content hashes, thresholds,
measure titles, source URLs, status labels, warnings - is used inside the app for pairing, safety
checks and the display, and is never sent on.

**Write-ins** are controlled by the dropdown in the Live results window:

* *named + one Write-in line* (default) - certified write-in candidates are sent under their own
  names, and the remaining write-in votes as a single "Write-in" line. The votes then add up to
  each county's printed total.
* *one Write-in line only* - every write-in vote in one line.
* *leave out* - ballot candidates only; the votes will be slightly short of the county total.

The write-in line is worked out as the county's contest total less the named candidates, which is
correct whether a county prints a write-in total row that already contains its named write-ins
(Monterey, Santa Cruz) or counts them on top (San Benito).

**Safety.** A race is held rather than sent when the API flags that county's copy as unreliable
(`worst_severity` of `error`, such as `total_mismatch`), when the API raises a county-wide error
(`votes_before_polls_close`, `election_date_mismatch`), or when the write-in rows contradict each
other. A county with nothing loaded (`not_started`) contributes nothing; a `zero_report` county is
sent as the zeros it is, with a note. The app also warns when the API's `election_date` differs
from the date it is sending under.

**Polling is cheap.** The app sends the API's ETag back on each check, so a poll that finds nothing
new costs one request and the screen simply notes "no change". The ballot definition is only
re-read when `ballot_version` moves.

The `sample/` folder holds a replica of the API to try against: `api_contests.json`,
`api_results.json` (Monterey still a zero report), `api_results_live.json` (all three counties
reporting) and the XML forms.

## Live results (any other JSON or XML feed)

Results use the same app, the same connection and the same message format as race setup.
Click **Live results...** in the main window to open the results window (it shares the
connection, election date, source name and TEST/LIVE setting of the main window).

1. **Set races up first** (CSV, send, import in Wire Manager). Election Manager links results
   to a race by Election + Source + Race ID, so every result goes out with the setup race's ID
   and the setup candidates' own spellings - never the feed's.
2. **Choose the feed:** a file path or a web address (optionally with headers such as
   `X-Api-Key: abc123`, separated by `;`). JSON or XML is detected automatically.
   If a feed's layout is unusual, add a **mapping file** that names where the contests,
   candidates, votes and so on are - see `sample/sample_feed_mapping.json`.
3. **Pairing.** Each feed contest is paired with a setup race, in this order:
   1. the **Feed ID** column in the setup CSV (add one if the feed has stable IDs - the surest way);
   2. a pairing you picked by hand in the grid (remembered in `%APPDATA%\ElectionRaceSetup\crosswalk.ini`);
   3. an automatic match on office, seat, district and place, taken only when there is a clear
      winner. "U.S. Senate" = "United States Senator", and different district or proposition
      numbers or different states never match.

   Candidates are matched by name inside the paired race (handles "Last, First", suffixes,
   accents, nicknames such as Mike/Michael, and "Write-in" lines).
4. **Status of each row:**

   | Status | Meaning |
   | --- | --- |
   | Ready | Paired, checked, and different from what was last sent |
   | Unchanged / Sent | Nothing new to send |
   | HELD | Paired but something looks wrong, so it is **not** sent automatically (see Notes) |
   | No match | No setup race found; pick one from the drop-down |

   A race is held when: a feed candidate matches nobody in the setup race, a vote count is not a
   whole number, a candidate's votes went **down** since the last send, precincts reporting is
   more than the total, or two feed contests point at the same race. Tick a held race and use
   **Send checked** to send it anyway (a corrected count, for example).
5. **Unattended use:** set "Check every (s)", tick "Send changes automatically" (off by default),
   and click **Start polling**. Only Ready races are sent, and only when something changed.
   Closing the results window hides it and polling carries on; quitting the app stops it.
6. **Manual use:** **Fetch now**, review, then **Send checked**.

**Feeds that report by county** (a `<county name="...">` around the contests, as in the Monterey /
Santa Cruz / San Benito feed): the same race usually appears once per county, often worded
differently ("Governor", "Treasurer" vs "State Treasurer", "Proposition 1" vs "1 - The Veterans
And Affordable Housing Bond Act Of 2026", "US Representative 18th Congressional District" vs
"United States Representative District 18"). The app pairs each county's contest with the setup
race separately, then **adds the counties together** so each setup race is sent once, with the
combined votes and precinct counts. The Counties column shows which counties went into each
row. Things that make this reliable:

* A trailing "(Vote for N)" is removed from contest names and kept as the number of winners.
* The **candidates** are used as evidence as well as the wording: the same names on the ballot
  pair two differently-worded contests, and a contest whose candidates share nobody with a
  setup race is never paired with it ("Governor" will not land on "Lieutenant Governor").
* Words that appear in few setup races (names, places) count for more than common ones
  ("District", "City"); "1 - ..." is read as Proposition 1 and "D - ..." as Measure D;
  common abbreviations are understood (USD, Equal., Appellate, Councilmember, 6th / Sixth).
* Lines flagged `write_in` and "Unqualified Write In" are ignored when they are not in the setup race.
* **Skip counties:** list counties to leave out (for example `San Benito`, whose feed is last
  June's primary). Skipped contests are not read, paired or sent.
* **Send county breakdown (sub-races):** also sends each county's own numbers inside the race
  (in the layout of the AP sample). Off by default; it has not been tried against Election
  Manager, so test it on a practice election.
* A race is held, not sent, if one county lists a candidate the setup race does not have, so a
  county with an unrelated ballot can never be quietly added in.

**When something will not pair:** click **Export pairing report...**. The CSV lists every feed
contest next to the setup race it was paired with (or the three closest, with scores), plus
setup races that received no feed data. Fix a leftover in one of three ways: pick the setup
race from the drop-down (remembered), put the feed's name for it in the setup CSV's **Feed ID**
column, or reword the setup race.

Sent for each race: votes, precincts reporting and total, and the feed's winner flag. Not yet
handled: county/sub-race breakdowns and percentages. Election Manager's own "Use Wire Winner
Flag" setting decides whether it acts on the winner flag.

Sample feeds in `sample/` pair with `sample_races.csv`: `sample_feed.json` (one layout),
`sample_feed.xml` (a very different one, with attributes and "Last, First" names), and
`sample_feed_mapping.json` (an explicit mapping for the XML). The JSON sample deliberately
includes a contest that has no setup race, so you can see the "No match" row.

`monterey_feed_excerpt.xml` reproduces the contest and candidate names of the Monterey and Santa
Cruz county feed (plus a few San Benito primary contests), with made-up vote counts.
`monterey_setup_races.csv` is a **stand-in** setup file whose race names are worded differently on
purpose; the tests pair the two. Your own setup CSV will word things its own way, so check the
pairing report on the first real fetch.

## Next steps

Candidate-level pairing by hand (for names the automatic rules cannot match) and reading feeds
that split one contest across several result groups that are not counties.
