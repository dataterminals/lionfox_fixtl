r"""Headless proof of /fixtl: boot a throwaway dedicated server with the built zip, Translocator Relocator (the keyed
translocators LionFox uses) and the test kit, join it with a fake client as FixFox (tests/FakeClient: a player over
TCP, no game, no sign-in), and run the kit's lanes on the server's real tick:

  works          a stone pair and a keyed pair send FixFox on their own; /fixtl finds nothing wrong and sends them too
  noclip         the 2026-11-07 case: /gm spectator, the game's mode packet, /gm survival. Vanilla leaves the server's
                 noclip on (fixtl's handler turns it off and says so); left on, no translocator notices FixFox; /fixtl
                 fixes it and sends them
  check          /fixtl check reports and changes nothing
  spectator      /fixtl explains, and doesn't send a spectator
  teleporting    Entity.Teleporting stuck: skipped; /fixtl clears it and sends them
  ignore         ignoreTeleportCall stuck: the translocator fires, nothing happens; /fixtl clears it and sends them
  stale-entry    a warm-up entry for an old copy of FixFox stuck mid-teleport: skipped; /fixtl drops it and sends them
  keyed-alone    the 2026-10-21 case: the only translocator keyed "wip"; /fixtl says so and names the look-alike "Wip "
  keyed-desync   block says "home", Translocator Relocator's list says "": never links, retyping "home" does nothing;
                 /fixtl re-lists it and sends them
  keyed-leftover a list entry where nothing stands and one for a block keyed otherwise: /fixtl cleans both, sends to B
  keyed-moved    B shows the key but is missing from the list (a CarryOn move): from A it looks partnerless;
                 /fixtl at A finds B in the loaded world, lists it and sends them; /fixtl keys flags it
  stone-nodest   a stone translocator linked to nowhere errors every tick; /fixtl sets it searching again
  stone-gone     its partner was picked up: /fixtl won't send them to the empty spot; /fixtl reroll is admin-only
  admin          /fixtl player NAME from the console fixes and sends them; a player can't run it for someone
  keys           /fixtl keys lists lone, unkeyed, shared-by-3 and look-alike keys
  cycle          Sylvia's 2026-11-12 report: a properly keyed pair stops working after spectator/creative/survival
                 switches. The fake game sends the real mode packet (as a view-distance change does) after each switch
                 out of survival. With fixtl's re-sync off the server's noclip is left on and the pair never notices
                 FixFox (/fixtl gets them through); with it on, the pair takes them by itself
  gm-matrix      her pattern every way round (3>1, 3>2>1, 2>3>1, 3>2>3>1, 2>1), with and without the packet, re-sync
                 off then on: off breaks exactly where a packet came in during noclip; on, the server's noclip is right
                 after every switch and the pair always takes FixFox
  stone-stalled  a search that never answers: left alone, then restarted two minutes after first seen, FixFox told

    dotnet build -c Release
    dotnet build -c Release tests/TestKit/FixtlTestKit.csproj
    dotnet build -c Release tests/FakeClient/FakeClient.csproj
    python tests/boot_test.py [--port 43091] [--fresh] [--payload DIR] [--specreturn ZIP] [--lanes NAME ...]

--specreturn adds our SpecReturn (it saves and restores the position around spectator trips) to check it plays no
part. --payload adds every *.zip in DIR (the live set), so the lanes run inside the real modpack; then only error lines
naming this mod, its kit or the fake client count. Needs the game at D:\Vintagestory, H:\Vintagestory or
$VINTAGE_STORY. The world lives in %TEMP%\fixtl-boot-test (reused unless --fresh) with VerifyPlayerAuth off, and the
server gets that folder as its TMP/TEMP. Exits 1 on a FAIL, a missing result or an unexpected error line. Built on
lionfox_Helper's.
"""
import argparse, os, queue, re, shutil, subprocess, sys, tempfile, threading, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LANES = ("works", "noclip", "check", "spectator", "teleporting", "ignore", "stale-entry", "keyed-alone", "keyed-desync",
         "keyed-leftover", "keyed-moved", "stone-nodest", "stone-gone", "admin", "keys", "cycle", "gm-matrix", "stone-stalled")
PLAYER = "FixFox"
RELOC = os.path.join(ROOT, "tests", "deps", "TranslocatorRelocatorFork_1.0.5.zip")


def game_dir():
    for d in (os.environ.get("VINTAGE_STORY"), r"D:\Vintagestory", r"H:\Vintagestory"):
        if d and os.path.exists(os.path.join(d, "VintagestoryServer.exe")):
            return d
    sys.exit("no VintagestoryServer.exe (set VINTAGE_STORY)")


def run(work, data, mods, port, transcript, lanes=()):
    gd = game_dir()
    env = dict(os.environ, TMP=os.path.join(work, "tmp"), TEMP=os.path.join(work, "tmp"))
    os.makedirs(env["TMP"], exist_ok=True)
    subprocess.run([os.path.join(gd, "VintagestoryServer.exe"), "--dataPath", data, "--setconfig={VerifyPlayerAuth:false}"],
                   cwd=gd, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=120)

    t0 = time.time()
    q = queue.Queue()

    def reader(proc, src):
        for line in proc.stdout:
            q.put((time.time() - t0, src, line.rstrip("\n")))
        q.put((time.time() - t0, src, None))

    server = subprocess.Popen([os.path.join(gd, "VintagestoryServer.exe"), "--dataPath", data, "--addModPath", mods,
                               "--ip", "127.0.0.1", "--port", str(port), "--maxclients", "4"],
                              cwd=gd, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                              text=True, encoding="utf-8", errors="replace", bufsize=1)
    threading.Thread(target=reader, args=(server, "S"), daemon=True).start()
    lines = []

    def pump(until=None, src="S", timeout=10.0, quiet=1.5):
        start = time.time()
        seen = False
        while time.time() - start < timeout:
            try:
                t, s, line = q.get(timeout=0.25)
            except queue.Empty:
                if (until is None or seen) and time.time() - start > quiet:
                    break
                continue
            if line is None:
                if s == "S":
                    return seen
                continue
            entry = f"{t:9.3f} {s}| {line}"
            lines.append(entry); transcript.write(entry + "\n"); transcript.flush()
            if until and not seen and (src is None or s == src) and until in line:
                seen = True
                start = time.time() - (timeout - quiet)
        return seen

    def send(c):
        entry = f"{time.time() - t0:9.3f} S| >>> {c}"
        lines.append(entry); transcript.write(entry + "\n")
        server.stdin.write(c + "\n"); server.stdin.flush()

    fake = None
    up = pump(until="Dedicated Server now running", timeout=1500)   # the full modpack takes 10+ minutes
    if up:
        bindir = os.path.join(ROOT, "tests", "FakeClient", "bin")
        exe = os.path.join(bindir, "FixtlFakeClient.exe")
        cmd = [exe] if os.path.exists(exe) else ["dotnet", os.path.join(bindir, "FixtlFakeClient.dll")]
        fake = subprocess.Popen(cmd + ["--port", str(port), "--name", PLAYER], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                text=True, encoding="utf-8", errors="replace", bufsize=1)
        threading.Thread(target=reader, args=(fake, "C1"), daemon=True).start()
        if pump(until="loaded and playing", src=None, timeout=900):
            pump(timeout=8, quiet=8)
            send("/fixtltest run" + "".join(" " + l for l in lanes)); pump(until="[fixtltest] done", timeout=900)
    try:
        send("/stop"); pump(until="Stopped the server", timeout=60)
    except OSError:
        pass
    for p in [server] + ([fake] if fake else []):
        try:
            p.wait(timeout=30)
        except subprocess.TimeoutExpired:
            p.kill()
    return up, lines


def window(lines, lane):
    """The lines from a lane's begin marker to its end marker."""
    out, inside = [], False
    for l in lines:
        if f"[fixtltest] begin {lane}" in l:
            inside = True
        if inside:
            out.append(l)
        if f"[fixtltest] end {lane}" in l:
            break
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int)
    ap.add_argument("--fresh", action="store_true")
    ap.add_argument("--payload", help="folder of mod zips to load alongside (the live set)")
    ap.add_argument("--specreturn", help="lionfoxspecreturn zip to load too")
    ap.add_argument("--lanes", nargs="+", default=[], help="run only these lanes")
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    port = a.port or (43092 if a.payload else 43091)

    build = os.path.join(ROOT, "build")
    zips = sorted((os.path.join(build, f) for f in os.listdir(build) if f.startswith("lionfoxfixtl_") and f.endswith(".zip")),
                  key=os.path.getmtime) if os.path.isdir(build) else []
    kit = os.path.join(ROOT, "tests", "TestKit", "bin", "lionfoxfixtltestkit")
    fake = os.path.join(ROOT, "tests", "FakeClient", "bin", "FixtlFakeClient.dll")
    if not zips or not os.path.exists(os.path.join(kit, "modinfo.json")) or not os.path.exists(fake):
        sys.exit("build the mod, the test kit and the fake client first (see the docstring)")

    work = os.path.join(tempfile.gettempdir(), "fixtl-boot-test" + ("-payload" if a.payload else "") + ("-specreturn" if a.specreturn else ""))
    if a.fresh and os.path.exists(work):
        shutil.rmtree(work)
    data, mods = os.path.join(work, "data"), os.path.join(work, "mods")
    os.makedirs(data, exist_ok=True)
    shutil.rmtree(mods, ignore_errors=True)
    os.makedirs(mods)
    have_reloc = False
    if a.payload:
        for f in os.listdir(a.payload):
            if f.endswith(".zip") and not f.startswith("lionfoxfixtl_"):
                shutil.copy(os.path.join(a.payload, f), mods)
                have_reloc |= f.lower().startswith("translocatorrelocator")
    if not have_reloc:
        shutil.copy(RELOC, mods)
    if a.specreturn:
        for f in os.listdir(mods):
            if f.startswith("lionfoxspecreturn_"):
                os.remove(os.path.join(mods, f))
        shutil.copy(a.specreturn, mods)
    shutil.copy(zips[-1], mods)
    shutil.copytree(kit, os.path.join(mods, "lionfoxfixtltestkit"))

    with open(os.path.join(work, "transcript.txt"), "w", encoding="utf-8", errors="replace") as t:
        print(f"booting with {os.path.basename(zips[-1])} on port {port} ...", flush=True)
        up, lines = run(work, data, mods, port, t, a.lanes)
    if not up:
        sys.exit(f"never reached 'Dedicated Server now running'; see {work}\\transcript.txt")

    out = "\n".join(lines)
    results = [l.split("[fixtltest] ", 1)[1].strip() for l in lines if "[fixtltest] " in l and "S| >>>" not in l]
    lanes = tuple(a.lanes) or LANES
    checks = [("fixtl, Translocator Relocator and the kit loaded", all(m in out for m in ("lionfoxfixtl", "translocatorrelocatorfork", "lionfoxfixtltestkit"))),
              ("self-check ok, with Translocator Relocator found", re.search(r"\[lionfox_fixtl\] self-check ok.*Translocator Relocator found", out) is not None),
              ("FixFox joined", any("loaded and playing" in l for l in lines))]
    for name in lanes:
        checks.append((name, any(r.startswith(f"PASS {name}:") for r in results)))
    checks.append(("no FAIL line", not any(r.startswith("FAIL") for r in results)))

    # Evidence beyond the kit's own verdicts, from the server's and the fake client's own output.
    if "noclip" in lanes:
        w = "\n".join(window(lines, "noclip"))
        checks.append(("noclip: fixtl's handler caught vanilla leaving the server's noclip on after /gm survival",
                       re.search(r"\[lionfox_fixtl\] FixFox switched to Survival: the server's noclip was still True", w) is not None))
    if "stone-nodest" in lanes:
        w = "\n".join(window(lines, "stone-nodest"))
        checks.append(("stone-nodest: linked-to-nowhere really errors every tick (vanilla's own warning)",
                       "Exception when ticking Static Translocator" in w))
    if "cycle" in lanes:
        w = window(lines, "cycle")
        sent = sum(1 for l in w if "C1| " in l and "sent a mode-change packet" in l)
        checks.append((f"cycle: the fake game sent the real mode packets ({sent})", sent >= 8))
        if a.specreturn or a.payload:
            checks.append(("cycle: SpecReturn was there and took FixFox back after a spectator trip",
                           any("[SpecReturn] FixFox: teleported back" in l for l in w)))
    if "stone-stalled" in lanes:
        w = "\n".join(window(lines, "stone-stalled"))
        checks.append(("stone-stalled: FixFox was told in chat two minutes later", "/fixtl, two minutes later" in w))

    # Errors. The stone-nodest lane sets up a translocator that throws on purpose; its window's tick exceptions are
    # the vanilla stuck state being shown, not a fault.
    expected = set(window(lines, "stone-nodest")) if "stone-nodest" in lanes else set()
    errors = [l for l in lines if ("Error]" in l or "Exception" in l) and "S| >>>" not in l and l not in expected]
    ours = [l for l in errors if any(k in l.lower() for k in ("lionfox_fixtl", "lionfoxfixtl", "fixtltest", "fixtlfakeclient"))]
    checks.append(("no error line names the mod or its kit", not ours))
    if a.payload:
        errors = ours
    for name, ok in checks:
        print(("ok    " if ok else "FAIL  ") + name)
    for r in results:
        if not r.startswith("info") and not r.startswith("begin") and not r.startswith("end"):
            print("  " + r)
    print(f"{len(errors)} unexpected error lines" + "".join("\n  " + e for e in errors[:20]))
    print(f"transcript: {work}\\transcript.txt")
    sys.exit(0 if all(ok for _, ok in checks) and not errors else 1)


if __name__ == "__main__":
    main()
