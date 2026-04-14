"""
PULSAR Codec GUI — black/blue terminal-style frontend for the .NET CLI.
Launches legacy / legacyplan / bypass / packer modes and streams the
resulting summary.txt back into the window.

Run:
    python Tools/pulsar_gui.py
"""
from __future__ import annotations

import os
import queue
import subprocess
import sys
import threading
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, ttk

BG = "#000000"
FG = "#00C8FF"
FG_DIM = "#0077AA"
BORDER = "#00C8FF"
ACCENT = "#00FFCC"
ERR = "#FF5577"
FONT_MAIN = ("Consolas", 10)
FONT_TITLE = ("Consolas", 14, "bold")

REPO_ROOT = Path(__file__).resolve().parent.parent
ARTIFACTS = REPO_ROOT / "Artifacts" / "Output"

MODE_FOLDERS = {
    "legacy":     ARTIFACTS / "Legacy",
    "legacyplan": ARTIFACTS / "Legacy",
    "bypass":     ARTIFACTS / "Packer-Bypass",
    "packer":     ARTIFACTS / "Packer",
}


def find_dll() -> Path | None:
    for cfg in ("Release", "Debug"):
        p = REPO_ROOT / "bin" / cfg / "net8.0" / "PulsarCodec.dll"
        if p.exists():
            return p
    return None


def build_command(mode: str, input_wav: Path, quality: int, block_size: int) -> list[str] | None:
    dll = find_dll()
    if dll is None:
        return None

    stem = input_wav.stem
    work = REPO_ROOT / "Artifacts" / "out-gui"
    work.mkdir(parents=True, exist_ok=True)

    if mode == "legacy":
        out = work / f"{stem}-legacy.wav"
        return ["dotnet", str(dll), "--legacy", str(input_wav), str(out), str(block_size)]
    if mode == "legacyplan":
        out = work / f"{stem}-legacyplan.wav"
        return ["dotnet", str(dll), "--legacyplan", str(input_wav), str(out)]
    if mode == "bypass":
        out = work / f"{stem}-v{quality}-bypass.wav"
        return ["dotnet", str(dll), "-V", str(quality), "--vbr", str(input_wav), str(out)]
    if mode == "packer":
        arc = work / f"{stem}-v{quality}.pulsr"
        dec = work / f"{stem}-v{quality}-decoded.wav"
        return ["dotnet", str(dll), "-V", str(quality), "--vbrplsr", str(input_wav), str(arc), str(dec)]
    return None


def newest_summary(mode_folder: Path, stem: str, since_mtime: float) -> Path | None:
    if not mode_folder.exists():
        return None
    candidates = [
        d for d in mode_folder.iterdir()
        if d.is_dir() and d.name.startswith(stem) and d.stat().st_mtime >= since_mtime - 2
    ]
    if not candidates:
        return None
    newest = max(candidates, key=lambda d: d.stat().st_mtime)
    summary = newest / "summary.txt"
    return summary if summary.exists() else None


class PulsarGui(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("PULSAR Codec")
        self.configure(bg=BG)
        self.geometry("900x700")
        self.minsize(780, 600)

        self.input_path = tk.StringVar()
        self.mode = tk.StringVar(value="legacy")
        self.quality = tk.IntVar(value=5)
        self.block_size = tk.IntVar(value=2048)
        self.status = tk.StringVar(value="idle")
        self._proc_queue: queue.Queue[tuple[str, str]] = queue.Queue()
        self._worker: threading.Thread | None = None

        self._build_style()
        self._build_layout()
        self.after(100, self._drain_queue)

    def _build_style(self) -> None:
        style = ttk.Style(self)
        try:
            style.theme_use("clam")
        except tk.TclError:
            pass
        style.configure("TRadiobutton", background=BG, foreground=FG,
                        focuscolor=BG, font=FONT_MAIN)
        style.map("TRadiobutton",
                  background=[("active", BG), ("selected", BG)],
                  foreground=[("active", ACCENT), ("selected", ACCENT)])
        style.configure("Pulsar.Horizontal.TScale",
                        background=BG, troughcolor="#002030",
                        bordercolor=BORDER, lightcolor=BORDER, darkcolor=BORDER)

    def _frame(self, parent: tk.Widget, title: str) -> tk.LabelFrame:
        f = tk.LabelFrame(parent, text=f" {title} ",
                          bg=BG, fg=FG, bd=1, relief="solid",
                          highlightbackground=BORDER, highlightcolor=BORDER,
                          font=FONT_MAIN, labelanchor="nw")
        return f

    def _entry(self, parent: tk.Widget, textvariable: tk.StringVar, width: int = 60) -> tk.Entry:
        return tk.Entry(parent, textvariable=textvariable,
                        bg=BG, fg=FG, insertbackground=FG,
                        disabledbackground=BG, disabledforeground=FG_DIM,
                        relief="solid", bd=1,
                        highlightbackground=BORDER, highlightcolor=ACCENT,
                        highlightthickness=1, font=FONT_MAIN, width=width)

    def _button(self, parent: tk.Widget, text: str, command) -> tk.Button:
        return tk.Button(parent, text=text, command=command,
                         bg=BG, fg=FG, activebackground="#002030",
                         activeforeground=ACCENT, relief="solid", bd=1,
                         highlightbackground=BORDER,
                         font=FONT_MAIN, padx=12, pady=4, cursor="hand2")

    def _build_layout(self) -> None:
        header = tk.Label(self, text="PULSAR CODEC", bg=BG, fg=FG, font=FONT_TITLE)
        header.pack(pady=(10, 4))

        # Input row
        input_frame = self._frame(self, "Input")
        input_frame.pack(fill="x", padx=12, pady=6)
        row = tk.Frame(input_frame, bg=BG)
        row.pack(fill="x", padx=8, pady=8)
        tk.Label(row, text="WAV file:", bg=BG, fg=FG, font=FONT_MAIN).pack(side="left")
        self._entry(row, self.input_path).pack(side="left", fill="x", expand=True, padx=8)
        self._button(row, "Browse…", self._pick_file).pack(side="left")

        # Mode row
        mode_frame = self._frame(self, "Mode")
        mode_frame.pack(fill="x", padx=12, pady=6)
        modes = [
            ("legacy",     "Legacy (fixed block, no quant)"),
            ("legacyplan", "LegacyPlan (planner, no quant)"),
            ("bypass",     "Bypass (VBR, in-memory)"),
            ("packer",     "Packer (VBR + .pulsr archive)"),
        ]
        for key, label in modes:
            rb = ttk.Radiobutton(mode_frame, text=label, variable=self.mode,
                                 value=key, command=self._refresh_param_state)
            rb.pack(anchor="w", padx=10, pady=2)

        # Params
        params_frame = self._frame(self, "Parameters")
        params_frame.pack(fill="x", padx=12, pady=6)
        prow = tk.Frame(params_frame, bg=BG)
        prow.pack(fill="x", padx=8, pady=8)

        tk.Label(prow, text="Quality (-V):", bg=BG, fg=FG, font=FONT_MAIN).grid(row=0, column=0, sticky="w")
        self.quality_scale = tk.Scale(prow, from_=0, to=9, orient="horizontal",
                                      variable=self.quality, bg=BG, fg=FG,
                                      troughcolor="#002030", activebackground=ACCENT,
                                      highlightbackground=BG, font=FONT_MAIN,
                                      length=280, showvalue=True)
        self.quality_scale.grid(row=0, column=1, sticky="we", padx=10)

        tk.Label(prow, text="Block size (legacy):", bg=BG, fg=FG, font=FONT_MAIN).grid(row=1, column=0, sticky="w", pady=(8, 0))
        self.block_entry = self._entry(prow, tk.StringVar(), width=10)
        self.block_entry.configure(textvariable=self.block_size)
        self.block_entry.grid(row=1, column=1, sticky="w", padx=10, pady=(8, 0))
        prow.columnconfigure(1, weight=1)

        # Run row
        action = tk.Frame(self, bg=BG)
        action.pack(fill="x", padx=12, pady=6)
        self.run_btn = self._button(action, "▶  RUN", self._run)
        self.run_btn.pack(side="left")
        tk.Label(action, text="status:", bg=BG, fg=FG_DIM, font=FONT_MAIN).pack(side="left", padx=(18, 4))
        tk.Label(action, textvariable=self.status, bg=BG, fg=ACCENT, font=FONT_MAIN).pack(side="left")

        # Output log + summary
        out_frame = self._frame(self, "Engine log")
        out_frame.pack(fill="both", expand=True, padx=12, pady=(6, 4))
        self.log_text = tk.Text(out_frame, bg=BG, fg=FG, insertbackground=FG,
                                relief="flat", font=FONT_MAIN, height=10,
                                highlightbackground=BORDER, highlightthickness=1,
                                wrap="word")
        self.log_text.pack(fill="both", expand=True, padx=6, pady=6)
        self.log_text.tag_configure("err", foreground=ERR)
        self.log_text.tag_configure("ok", foreground=ACCENT)

        sum_frame = self._frame(self, "Latest summary.txt")
        sum_frame.pack(fill="both", expand=True, padx=12, pady=(4, 10))
        self.summary_text = tk.Text(sum_frame, bg=BG, fg=FG, insertbackground=FG,
                                    relief="flat", font=FONT_MAIN, height=12,
                                    highlightbackground=BORDER, highlightthickness=1,
                                    wrap="word")
        self.summary_text.pack(fill="both", expand=True, padx=6, pady=6)

        self._refresh_param_state()

    def _refresh_param_state(self) -> None:
        mode = self.mode.get()
        needs_quality = mode in ("bypass", "packer")
        needs_block = mode == "legacy"
        self.quality_scale.configure(state="normal" if needs_quality else "disabled",
                                     fg=FG if needs_quality else FG_DIM)
        self.block_entry.configure(state="normal" if needs_block else "disabled")

    def _pick_file(self) -> None:
        start_dir = str(REPO_ROOT / "Artifacts" / "Test Tracks") \
            if (REPO_ROOT / "Artifacts" / "Test Tracks").exists() else str(REPO_ROOT)
        path = filedialog.askopenfilename(
            title="Select WAV",
            initialdir=start_dir,
            filetypes=[("WAV files", "*.wav"), ("All files", "*.*")],
        )
        if path:
            self.input_path.set(path)

    def _append_log(self, text: str, tag: str | None = None) -> None:
        self.log_text.insert("end", text, tag)
        self.log_text.see("end")

    def _set_summary(self, text: str) -> None:
        self.summary_text.delete("1.0", "end")
        self.summary_text.insert("1.0", text)

    def _run(self) -> None:
        if self._worker and self._worker.is_alive():
            return
        input_str = self.input_path.get().strip()
        if not input_str or not Path(input_str).is_file():
            self._append_log("[error] select a valid input WAV\n", "err")
            return
        input_path = Path(input_str)

        cmd = build_command(self.mode.get(), input_path, self.quality.get(), self.block_size.get())
        if cmd is None:
            self._append_log("[error] PulsarCodec.dll not found. Build with:\n"
                             "        dotnet build PulsarCodec.csproj -c Release\n", "err")
            return

        self.log_text.delete("1.0", "end")
        self.summary_text.delete("1.0", "end")
        self._append_log("> " + " ".join(f'"{c}"' if " " in c else c for c in cmd) + "\n\n", "ok")
        self.status.set("running…")
        self.run_btn.configure(state="disabled")

        mode_folder = MODE_FOLDERS[self.mode.get()]
        started_at = mode_folder.stat().st_mtime if mode_folder.exists() else 0.0

        self._worker = threading.Thread(
            target=self._run_worker,
            args=(cmd, mode_folder, input_path.stem, started_at),
            daemon=True,
        )
        self._worker.start()

    def _run_worker(self, cmd: list[str], mode_folder: Path, stem: str, started_at: float) -> None:
        try:
            proc = subprocess.Popen(
                cmd,
                cwd=str(REPO_ROOT),
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                encoding="utf-8",
                errors="replace",
                bufsize=1,
            )
            assert proc.stdout is not None
            for line in proc.stdout:
                self._proc_queue.put(("log", line))
            rc = proc.wait()
            if rc == 0:
                self._proc_queue.put(("done_ok", ""))
            else:
                self._proc_queue.put(("done_err", f"\n[exit {rc}]\n"))
        except Exception as exc:
            self._proc_queue.put(("done_err", f"\n[exception] {exc}\n"))
            return

        summary = newest_summary(mode_folder, stem, started_at)
        if summary is None:
            self._proc_queue.put(("summary", "(no summary.txt found)"))
        else:
            try:
                self._proc_queue.put(("summary", summary.read_text(encoding="utf-8")))
                self._proc_queue.put(("log", f"\n[summary: {summary}]\n"))
            except OSError as exc:
                self._proc_queue.put(("summary", f"(failed to read summary: {exc})"))

    def _drain_queue(self) -> None:
        try:
            while True:
                kind, payload = self._proc_queue.get_nowait()
                if kind == "log":
                    self._append_log(payload)
                elif kind == "done_ok":
                    self.status.set("done")
                    self.run_btn.configure(state="normal")
                elif kind == "done_err":
                    self._append_log(payload, "err")
                    self.status.set("failed")
                    self.run_btn.configure(state="normal")
                elif kind == "summary":
                    self._set_summary(payload)
        except queue.Empty:
            pass
        self.after(80, self._drain_queue)


def main() -> int:
    app = PulsarGui()
    app.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
