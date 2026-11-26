# Getting started

SimpleScanner is a communications scanner for SDRplay RSP receivers. It receives AM, narrow FM and broadcast FM, decodes five digital voice modes, keeps your channels in groups, scans them quickly, and can run several radios at once.

## What you need

- An SDRplay RSP receiver and an antenna (see **Hardware**).
- The SDRplay API, version 3. It is SDRplay's own software and is not part of SimpleScanner: download its installer from sdrplay.com/api and run it before the first start.
- For the RadioReference browser only: your own radioreference.com account with a Premium subscription, and an application key (see **RadioReference**).

## The first start on a Mac

- Open the disk image and drag SimpleScanner to Applications.
- The app is not notarized by Apple, so macOS refuses it the first time. Open System Settings > Privacy & Security, find the line about SimpleScanner, and press **Open Anyway**. This is needed once.
- Close any other SDR program first (SDRconnect, SDRuno, CubicSDR…). A radio can be used by one program at a time, and one that another program holds is not listed.

## Hear something in five steps

- Plug the radio in and start SimpleScanner. With more than one radio plugged in, it asks which to use.
- Press the round power button on the display. It turns green when the radio is running.
- On the **Operate** tab, type a frequency in MHz in the Tune box and press Enter. A NOAA weather channel (162.400 to 162.550) or a broadcast FM station is a good first test.
- Slide **Squelch** until the green mark on the level bar sits just above the bar's resting level. The RX lamp lights when the squelch opens.
- Set **Volume**. On an RSPdx, check on the **Settings** tab that **Antenna** is the socket your antenna is on.

## Then fill it with channels

- Open **Memory Mgr** and load the frequencies for your area: **Import RadioReference CSV…** for a downloaded file, or **RadioReference online…** to browse by place. **RadioReference** explains both.
- Or tune to something by hand and keep it with **Store** on the **Scan Groups** tab.
- Tick the groups you want on **Scan Groups** and press **Scan**.

# The display

The display shows what a scanner shows: the frequency, the channel's name and whose it is, the mode, and the signal level.

- **Power button**: starts and stops the radio. Green when running.
- **Frequency**: roll the mouse wheel over a digit to tune by that digit, or click a digit's top or bottom half to step it.
- **Level bar**: the signal on the channel. The green mark is where the squelch opens.
- **RX**: lit while the squelch is open.
- **OVERLOAD**: the radio's front end has more signal than it can take. With Auto gain on, the scanner steps the gain down by itself.
- **ENCRYPTED**: the call is encrypted. It is shown and stays silent.
- **MUTE** and **STEREO**: the speaker is muted; a broadcast FM station is being received in stereo.
- For a stored channel the display also shows its tag, tone or code, station class, repeater input and callsign. For digital voice it shows who is talking (see **Digital voice**).

## The small window

The button with two arrows shrinks the window to the display alone, with Mute, Scan, Skip and Avoid under it. The same button, or **Full window**, brings the full window back. Each view remembers its own size and place.

# Tuning and listening

The **Operate** tab works the radio.

## Tune

- Type a frequency in MHz and press Enter or **Go**. The receiver covers 0.1 to 2000 MHz.
- The arrow buttons move one step. **Step** sets the size; Auto uses the usual channel spacing of the band.
- Or tune on the display's digits with the mouse wheel.

## Mode and filter

- **Auto** takes the mode and filter from a United States band plan: AM on the airband, FM elsewhere.
- **AM**, **NFM** (narrow FM: public service, business, marine, weather, amateur) and **WFM** (broadcast FM) set it by hand.
- **Stereo** receives broadcast FM in stereo when the station sends it. Mono is quieter on a weak station.
- **Filter** is the channel filter. Use the narrow one where channels are 12.5 kHz (FM) or 8.33 kHz (AM) apart.
- There is no SSB or CW reception.

## Audio and squelch

- **Volume** and **Mute**. Mute silences the speaker without touching the volume.
- **Squelch**: set the green mark just above the bar's resting level. All the way left is squelch off.
- **Auto** squelch also stays shut on noise, however strong: a channel must be above the mark and carry a clean signal. Useful when one squelch setting has to serve quiet and noisy bands.

## Blocked bands

The cellular telephone bands (824 to 849 and 869 to 894 MHz) are blocked: they cannot be received, and frequencies in them are left out of imports.

# Channels and groups

A stored channel is a frequency with a name and what is known about it. Channels live in groups, and a channel can be in as many groups as you like.

## Three kinds of group

- **User groups**: the ones you make, in the channel editor or by naming a group when importing.
- **Auto groups by location**: made by the program, one for each location in use (a town, a county, statewide, nationwide).
- **Auto groups by tag**: made by the program, one for each tag in use (Fire Dispatch, Law Tac, Aircraft…).

## The Scan Groups tab

- Tick one or several groups to show and scan. With none ticked, all are. **All groups** unticks every one.
- The list at the right shows the channels of the ticked groups. Tick a channel to include it in the scan; select one to tune to it.
- **Store** keeps the frequency on the display under the name typed beside it. With one group shown it goes in that group; otherwise in "My channels".
- **Delete** removes the selected channel.

## The channel editor (Memory Mgr)

**Memory Mgr** opens the channel editor in a window of its own.

- **Filter**: type any words from the name, agency, description, tag, tone, callsign or frequency. The box beside it shows one group only, or the channels in no group.
- Click a column heading to sort; drag a heading's edge to change its width.
- Select a channel to edit its fields at the right. **Receive as** sets the mode and filter used for that channel.
- Select several (Shift or Command click) to tick or untick them for scanning, delete them, or put them in a group. **Add to group** makes the group if it is new.
- Double-click a channel to tune the radio to it.
- **Rename or delete group**: deleting a group leaves its channels stored.
- **Duplicates** lists the channels stored more than once, set by set. Choose what counts as the same channel; **Select the extras** selects all but the fullest of each set, ready for Delete. Nothing is deleted until you press Delete.

## What is not used yet

A stored tone or code (PL, DPL, NAC, color code) is shown on the display but is not yet used to decide what the scan stops on.

# Scanning and searching

## Scan

**Scan** visits the ticked channels of the ticked groups. It looks at several megahertz at a time, so channels that are close together are checked at once.

- The scan stops on a channel while there is a signal above the squelch mark, and for two seconds after.
- **Skip** leaves the channel the scan has stopped on and carries on.
- **Avoid** leaves it and keeps it out of the scan for the **Avoid Timer** set on Settings (30 minutes, 1 hour, or the full session). Closing the app clears every avoid.
- **Resume Timer** (Settings) is the longest the scan stays on a busy channel before it carries on, from 15 seconds to 10 minutes, or **Hold** to stay until the signal drops. A channel left this way is passed by for ten seconds so the others get a turn.

## The frequency list on Operate

Under the groups at the right of Operate are the frequencies of the ticked groups, each with a tick. Untick one to leave it out of the scan. It is the channel's own Scan tick, the same one as on Scan Groups. A change takes effect at once, also on a running scan.

## Search

The **Search** tab looks through a range of frequencies for anything above the squelch mark.

- Type the lowest and the highest frequency in MHz and press the search button.
- It uses the band's usual channel spacing unless a Step is chosen on Operate.
- **Skip** leaves a frequency out for ten minutes. To keep one, go to Scan Groups and press **Store**.

# Digital voice

Digital voice is recognised by itself on any narrow FM channel. Nothing has to be set, and what is written in a channel's Signal type field does not matter.

- **P25 Phase 1** (conventional): speech, with the NAC, talkgroup and radio ID.
- **DMR** (Tier II conventional: repeaters, and mobiles heard directly): speech, with the color code, time slot, talkgroup, radio ID and talker alias. It follows one of the two time slots at a time: the first with a call.
- **NXDN** (conventional, 4800 and 9600 bit/s): speech, with the RAN, talkgroup and radio ID. Calls in NXDN's full-rate voice mode are shown, not decoded.
- **D-STAR**: speech, with the callsigns and the transmission's short text message.
- **C4FM (Yaesu System Fusion)**: speech in DN, VW and the other voice and data mode, with the callsigns and the digital group ID.

## What it does not do

- No trunk following, and no P25 Phase 2. Trunked systems' frequencies can be stored, but are left unticked.
- Encrypted calls are shown and stay silent.
- DMR, NXDN and System Fusion DN speech uses the AMBE+2 codec. A build made without it still follows these signals and shows who is talking; the display then says "no voice codec" and the call is silent.

# RadioReference

RadioReference (radioreference.com) keeps a database of who uses which frequency. SimpleScanner can take frequencies from it in two ways, both reached from the channel editor (**Memory Mgr**):

- **Import RadioReference CSV…** reads a file you downloaded from the website yourself.
- **RadioReference online…** asks RadioReference's web service directly and lets you browse by place.

Both end the same way: stored channels in a group you name, which you can then edit, regroup and scan. They can be mixed, and a channel is not stored twice.

## CSV import

This is the simple way, and the one to use if you have no application key.

- Sign in at radioreference.com in your web browser, go to the frequency page of a county or an agency, and download it as CSV. RadioReference offers the download to its Premium subscribers.
- In the channel editor press **Import RadioReference CSV…** and choose the file. Several files can be chosen at once.
- For each file the editor shows what it found and asks for a group name. A county or town name is the usual choice. Press **Import**, or **Leave out** to pass the file by.

What to know about it:

- SimpleScanner itself needs no account, no application key and no connection for this. A file someone gave you works as well.
- The file must be a frequency download: it needs a "Frequency Output" column. A talkgroup download of a trunked system is refused.
- It reads the frequency, repeater input, alpha tag (which becomes the name), agency or category, description, tag, mode, tone, FCC callsign and station class.
- Importing the same file again adds nothing twice. A channel already stored is kept as it is, with your edits, and joins the group named.
- Frequencies of a trunked system's sites (tag TRS) are stored but not ticked for scanning.
- A CSV file does not say where a channel is used, so its channels have no location, and no location group, unless that place has also been fetched online or you type the location in the editor.

## RadioReference online

The browser is a window of its own: sign in, choose a place, tick what you want, load it.

It needs two things:

- **Your own RadioReference account with a Premium subscription.** RadioReference's web service answers Premium subscribers only.
- **An application key.** RadioReference issues these to the authors of programs that use its web service. None comes with SimpleScanner. Enter the one issued to you under **Application key…** at the top right of the window. It is kept in a file beside the settings, readable only by you. Save with the box empty to remove it.

Using it:

- Type your user name and password and press **Sign in**. On a Mac, **Keep in the keychain** keeps the password in your login keychain, so that you are signed in when the window next opens. **Sign out** forgets it.
- Choose a place: type a United States ZIP code and press **Find**, or choose a country, a state or province, and a county. With a ZIP code, each list shows its distance from it.
- The tree at the left shows what the place has: categories, agencies, and the frequency lists under them. Click a list (a line with no arrow) to see its frequencies at the right.
- Tick the lists you want, check the group name beside **Load** (it starts as the county's name), and press **Load**.

What to know about it:

- What is fetched is kept on this computer for a week, so browsing a place again is quick and does not ask RadioReference again. **Fetch again** asks afresh. After signing out, what was fetched before can still be browsed.
- Each channel gets a location (the town where RadioReference lists a county by town, otherwise the county, "statewide" or "nationwide"), so the location groups fill by themselves.
- Channels already stored are not added twice, including ones that came from a CSV file of the same place.
- Telemetry (data) channels are stored but not ticked for scanning.
- Trunked systems are listed for information, with no tick box: the scanner does not follow them yet.
- Where RadioReference marks a channel as encrypted, its signal type ends in "e" (some of the traffic) or "E" (all of it).

## The difference in short

- **What you need.** CSV: only the file. Online: a Premium account, an application key, and a connection.
- **How you choose.** CSV: whatever is in the file, one county or agency at a time. Online: browse the tree and tick exactly the categories and agencies wanted, with a preview of each.
- **Location groups.** CSV: none by itself. Online: every channel gets its location.
- **Trunked systems.** CSV: site frequencies are stored unticked. Online: shown, not loaded.
- **Telemetry.** CSV: stored like any other channel. Online: stored unticked.
- **Keeping up to date.** CSV: download and import the file again. Online: Fetch again, then Load; only what is new is added.

In both, a channel you have edited is never overwritten by a later import.

# Several radios

With more than one RSP plugged in, the scanner can run them together.

## Setting it up

- On the **Settings** tab, choose a radio in the **Receiver** box and tick **Use**. Do this for each radio wanted.
- Close and start the app. The radios ticked are started together from then on, whenever they are plugged in.
- With none ticked, the radio used last is used, as with one radio.

## How it works

- Each radio has a strip at the top of the window: power, frequency, channel, mode, level, and its own Scan, Skip and Avoid buttons.
- Click a strip to select that radio. Tune, Mode, Filter, Audio, Search and the Scan row on Operate work the radio selected. A click on the frequency of a radio that is not selected only selects it; once it is selected, its digits tune by wheel or click as the big display's do.
- Each radio has its own ticked groups. The groups shown are those of the radio selected, and the heading says whose they are.
- **Scan** starts and stops the radio selected and leaves the others as they are: one can sit on a fixed channel while another scans.
- When two radios scan channels they have in common, those are shared out between them, so each is visited by one radio and the scan comes round sooner. They are shared out again when one stops.
- A channel's own Scan tick is the same for every radio.
- Each radio keeps its own settings (antenna, gain, squelch, volume and the rest) under its serial number. **Pan** on Settings puts a radio's sound to the left or the right, to tell them apart by ear.
- Each radio runs in a helper process of its own, started and ended by the program: the SDRplay software gives a program one radio, and a radio that stops working is restarted in a fresh helper (see **Troubleshooting**).

## USB: each radio needs room on the bus

> Running several radios depends on a USB bus with enough bandwidth to carry them all. Every RSP is a USB 2.0 device and sends the scanner a wide slice of spectrum without a pause: about 7.7 million samples a second, a large share of what one USB 2.0 connection (480 Mbit/s) can carry. Two radios that share one such connection will not both fit.

- Plug each radio into its own port on the computer, not into a shared hub.
- A USB 3 hub does not help: USB 2.0 devices on it still share the hub's single USB 2.0 link to the computer.
- Keyboards, audio interfaces, cameras and disks on the same hub take their share too.
- Signs of a bus that is too full: breaks or stutter in the sound, a radio that will not start or stops by itself, a scan that slows down, or trouble that appears only when the second radio is switched on.
- If it happens, move one radio to another port (on a desktop, one at the front and one at the back are often on separate controllers), or put it on a Thunderbolt dock, which has a USB controller of its own.

# Settings

## Output

Which sound device the audio goes to. It can be changed while receiving. **Refresh** looks again after one is plugged in.

## Radio

- **Auto gain**: the scanner steps the radio's front-end attenuation up on overload and back down when it clears.
- **LNA step**: front-end attenuation; 0 is the most gain. With Auto gain on, this is the most gain the scanner may use.
- **Receiver**: the radio these settings belong to, when more than one is plugged in. A radio another program is using is not listed.
- **Antenna**: the antenna socket, on models with more than one that the app can switch (see **Hardware**).
- **Use**: run this radio whenever it is plugged in (see **Several radios**). Takes effect at the next start.
- **MW/FM broadcast notch**: switches in the radio's notch filters for the AM (medium wave, about 0.5 to 1.7 MHz) and FM (88 to 108 MHz) broadcast bands, so strong broadcast stations cannot overload it while it listens elsewhere. One switch covers both bands. Turn it off to listen to AM or FM broadcast stations.
- **Correction**: frequency correction in parts per million. Leave at 0 unless signals on known frequencies all read off by the same proportion.
- **Pan**: where this radio's sound sits between the left and right speaker.
- **Enable bias T**: puts DC (about 4.7 V) on the antenna socket to power a masthead preamplifier or an active antenna. On an RSPdx it is on Antenna B only. Leave it off unless something on that socket needs the power.

## Scan

**Avoid Timer** and **Resume Timer**: see **Scanning and searching**.

# Hardware

SimpleScanner works SDRplay RSP receivers through SDRplay's API version 3. It does not work other makes of SDR (RTL-SDR, Airspy, HackRF and so on).

## The RSP models

- **RSPdx** and **RSPdx-R2**: the models the app was written for. Antenna A, B and C can be chosen; bias T (on Antenna B) and the MW/FM broadcast notch are supported.
- **RSP1A** and **RSP1B**: one antenna socket; bias T and the MW/FM broadcast notch are supported.
- **RSP1**: one antenna socket. It has no bias T and no notch filter.
- **RSP2** and **RSP2pro**: received on Antenna A; the app does not switch to Antenna B or the Hi-Z port. Bias T is on Antenna B, so it is of no use here; the MW/FM broadcast notch is supported.
- **RSPduo**: used as one receiver, on its first tuner only. Bias T and the notch are not offered.

## What has been tried

An RSPdx alone, and an RSPdx with an RSP1 as two radios, are what the app has been run with. The other models are opened the same way through the same API and should stream, but have not been tried. Reports are welcome.

## Limits that apply to every model

- Coverage in the app is 0.1 to 2000 MHz, less the blocked cellular bands.
- A radio can be held by one program at a time. Close other SDR programs before starting, and wait a few seconds after closing one.
- The computer needs the SDRplay API installed and its service running. If the app says the API is not installed or its service is not answering, install it from sdrplay.com/api, or restart the service or the computer.
- Several radios at once need USB bandwidth for each (see **Several radios**).

## Antennas

A scanner is only as good as its antenna. A discone or a wideband scanner antenna outdoors and clear of the building does far more than any setting in the app. If strong broadcast stations nearby cause OVERLOAD, switch on the MW/FM broadcast notch and leave Auto gain on.

# Troubleshooting

- **"No SDRplay radio found"**: check the cable, and that no other program (or another copy of SimpleScanner) has the radio.
- **"The SDRplay API is not installed"**: install it from sdrplay.com/api and start again.
- **Running but silent**: check Mute, Volume, the Output device on Settings, and that the squelch mark is below the signal. Check the Antenna socket chosen.
- **The scan never stops, or stops on noise**: the squelch mark is too high, or too low. Set it just above the bar's resting level, or switch on Auto squelch.
- **OVERLOAD shows often**: leave Auto gain on, raise the LNA step, and switch on the MW/FM broadcast notch.
- **A digital call is shown but silent**: it is encrypted (ENCRYPTED shows), or it is a kind the app shows without decoding, or this build has no AMBE+2 codec ("no voice codec").
- **Stutter with two radios**: the USB bus is too full. See **Several radios**.
- **A radio stops by itself after a while** (most often an original RSP1, after half an hour or more of scanning): its USB link has jammed, or the SDRplay software reports it unplugged. The app notices within a few seconds, resets the radio's USB connection, and starts it again in a fresh helper with the same settings and scan; the sound drops for about fifteen seconds. If it stops again within a minute the app gives up and says so: unplug the radio, plug it back in, and switch it on. Each event is written to `~/Library/Logs/SimpleScanner.log` (macOS); the USB reset is done on macOS only.
- **The RadioReference browser will not sign in**: it needs both an application key and a Premium subscription. The line under the account bar says which is missing.

# Files and privacy

- Settings and the database of stored channels are in your Library folder, under Application Support/SimpleScanner. The database (simplescanner.db) also holds what was fetched from RadioReference.
- The RadioReference application key is in the file radioreference.key in the same folder, readable only by you. The password is kept only if you ask, in the macOS login keychain.
- To move your channels to another computer, close the app and copy that folder.
- The app talks to the network only for RadioReference online, and only when you use it.

# About

SimpleScanner is free software under the GPL-3.0 licence. The source is at github.com/kingsqueak/SimpleScanner.

It builds on miniaudio (audio output), RadioFoundry and DSD (digital voice receivers), and mbelib (speech decoders), each under its own licence, and on Avalonia for the window.

SDRplay and RSP are SDRplay's names; RadioReference is RadioReference.com's. SimpleScanner is not connected with either.
