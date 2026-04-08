#!/usr/bin/env python3
"""
RuneReader Voice provider benchmark runner.

Runs a fixed, built-in benchmark corpus against a single server/provider,
saves each returned OGG with meaningful filenames for human listening review,
and emits structured JSON/CSV/Markdown outputs suitable for feeding into
an LLM to consolidate findings into your provider test notes.
"""
from __future__ import annotations

import argparse
import csv
import dataclasses
import datetime as dt
import hashlib
import json
import os
import re
import sys
import time
from pathlib import Path
from typing import Any, Iterable

try:
    import requests
except ImportError as exc:  # pragma: no cover
    raise SystemExit("This script requires the 'requests' package. Install with: pip install requests") from exc

WPM_FOR_TRUNCATION_HEURISTIC = 130.0
DEFAULT_TIMEOUT = 300.0
DEFAULT_POLL_INTERVAL = 0.5
RESULTS_JSON = "benchmark_results.json"
RESULTS_CSV = "benchmark_results.csv"
RESULTS_MD = "benchmark_summary.md"
LLM_PACKET_MD = "benchmark_llm_packet.md"
MANIFEST_JSON = "benchmark_manifest.json"

NUMBER_WORDS = {
    "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
    "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen",
    "nineteen", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety",
    "hundred", "thousand", "first", "second", "third", "fourth", "fifth", "sixth", "seventh",
    "eighth", "ninth", "tenth", "eleventh", "twelfth", "thirteenth", "fourteenth", "fifteenth",
    "sixteenth", "seventeenth", "eighteenth", "nineteenth", "twentieth",
    "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
    "january", "february", "march", "april", "may", "june", "july", "august", "september",
    "october", "november", "december",
}


@dataclasses.dataclass
class TestCase:
    test_code: str
    title: str
    section: str
    text: str
    source: str
    generated: bool = False


@dataclasses.dataclass
class RunResult:
    run_index: int
    test_code: str
    title: str
    section: str
    generated: bool
    provider_id: str
    server: str
    voice_mode: str
    voice_label: str
    cache_mode: str
    cache_bust_tag: str
    lang_code: str
    speech_rate: float
    cfg_weight: float | None
    exaggeration: float | None
    cfg_strength: float | None
    nfe_step: int | None
    cross_fade_duration: float | None
    sway_sampling_coef: float | None
    lux_num_steps: int | None
    lux_t_shift: float | None
    lux_return_smooth: bool | None
    cosy_instruct: str | None
    voice_instruct: str | None
    status: str
    submit_cached: bool | None
    header_cache: str | None
    cache_key: str | None
    progress_key: str | None
    input_chars_submit: int | None
    input_words_submit: int | None
    header_input_chars: int | None
    header_input_words: int | None
    synth_time_sec: float | None
    duration_sec: float | None
    realtime_factor: float | None
    expected_duration_sec: float | None
    duration_ratio_vs_expected: float | None
    likely_truncated: bool | None
    normalized_text_hash16: str
    raw_text_chars: int
    raw_text_words: int
    sentence_count: int
    has_list_markers: bool
    has_repeated_patterns: bool
    has_number_words_or_ordered_terms: bool
    has_digits: bool
    ogg_filename: str | None
    result_url: str | None
    error: str | None


class BenchmarkError(RuntimeError):
    pass


class RrvBenchmarkClient:
    def __init__(self, server: str, api_key: str | None, timeout: float) -> None:
        self.server = server.rstrip("/")
        self.timeout = timeout
        self.session = requests.Session()
        self.session.headers.update({"Accept": "application/json"})
        if api_key:
            self.session.headers["Authorization"] = f"Bearer {api_key}"

    def _url(self, path: str) -> str:
        return f"{self.server}{path}"

    def list_providers(self) -> list[dict[str, Any]]:
        resp = self.session.get(self._url("/api/v1/providers"), timeout=self.timeout)
        self._raise_for_status(resp)
        return resp.json()

    def get_provider(self, provider_id: str) -> dict[str, Any]:
        resp = self.session.get(self._url(f"/api/v1/providers/{provider_id}"), timeout=self.timeout)
        self._raise_for_status(resp)
        return resp.json()

    def get_voices(self, provider_id: str) -> list[dict[str, Any]]:
        resp = self.session.get(self._url(f"/api/v1/providers/{provider_id}/voices"), timeout=self.timeout)
        self._raise_for_status(resp)
        return resp.json()

    def get_samples(self, provider_id: str) -> list[dict[str, Any]]:
        resp = self.session.get(self._url(f"/api/v1/providers/{provider_id}/samples"), timeout=self.timeout)
        self._raise_for_status(resp)
        return resp.json()

    def submit_v2(self, payload: dict[str, Any]) -> dict[str, Any]:
        resp = self.session.post(self._url("/api/v1/synthesize/v2"), json=payload, timeout=self.timeout)
        self._raise_for_status(resp)
        return resp.json()

    def stream_progress_until_done(self, progress_key: str, progress_timeout: float) -> dict[str, Any]:
        url = self._url(f"/api/v1/synthesize/v2/{progress_key}/progress")
        last_event: dict[str, Any] | None = None
        started = time.monotonic()
        with self.session.get(url, stream=True, timeout=(self.timeout, progress_timeout)) as resp:
            self._raise_for_status(resp)
            event_type: str | None = None
            data_lines: list[str] = []
            for raw_line in resp.iter_lines(decode_unicode=True):
                if raw_line is None:
                    continue
                line = raw_line.strip()
                if line == "":
                    if data_lines:
                        payload_text = "\n".join(data_lines)
                        try:
                            payload = json.loads(payload_text)
                        except json.JSONDecodeError:
                            payload = {"raw": payload_text}
                        if event_type and "event" not in payload:
                            payload["event"] = event_type
                        last_event = payload
                        status = str(payload.get("status") or payload.get("event") or "").lower()
                        if status in {"complete", "error"}:
                            return payload
                    event_type = None
                    data_lines = []
                    if time.monotonic() - started > progress_timeout:
                        raise BenchmarkError(f"Timed out waiting for progress on job {progress_key}")
                    continue
                if line.startswith(":"):
                    continue
                if line.startswith("event:"):
                    event_type = line.split(":", 1)[1].strip()
                elif line.startswith("data:"):
                    data_lines.append(line.split(":", 1)[1].strip())
            if last_event is not None:
                return last_event
        raise BenchmarkError(f"Progress stream for job {progress_key} ended before a terminal event")

    def get_result(self, progress_key: str) -> requests.Response:
        resp = self.session.get(self._url(f"/api/v1/synthesize/v2/{progress_key}/result"), timeout=self.timeout)
        self._raise_for_status(resp)
        return resp

    @staticmethod
    def _raise_for_status(resp: requests.Response) -> None:
        try:
            resp.raise_for_status()
        except requests.HTTPError as exc:
            detail = ""
            try:
                payload = resp.json()
                if isinstance(payload, dict):
                    detail = payload.get("detail") or payload.get("error") or json.dumps(payload)
                else:
                    detail = json.dumps(payload)
            except Exception:
                detail = resp.text[:500]
            raise BenchmarkError(f"HTTP {resp.status_code} from {resp.request.method} {resp.url}: {detail}") from exc


def slugify(text: str, max_len: int = 80) -> str:
    text = text.lower().strip()
    text = re.sub(r"[^a-z0-9]+", "-", text)
    text = re.sub(r"-+", "-", text).strip("-")
    return text[:max_len] or "item"


def safe_short_hash(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Benchmark one RuneReader Voice provider against built-in and optional markdown test cases")
    parser.add_argument("--server", required=True, help="Base server URL, e.g. http://127.0.0.1:8000")
    parser.add_argument("--provider", required=True, help="Provider ID, e.g. chatterbox_full or cosyvoice")
    parser.add_argument("--api-key", default=os.getenv("RRV_API_KEY"), help="Bearer token for the server")
    parser.add_argument("--outdir", default=None, help="Output directory. Default: ./benchmark_runs/<provider>_<timestamp>")
    parser.add_argument("--timeout", type=float, default=DEFAULT_TIMEOUT, help="HTTP timeout in seconds")
    parser.add_argument("--progress-timeout", type=float, default=1800.0, help="SSE progress timeout per test in seconds")
    parser.add_argument("--poll-interval", type=float, default=DEFAULT_POLL_INTERVAL, help="Reserved for future polling mode")
    parser.add_argument("--list-providers", action="store_true", help="List providers and exit")
    parser.add_argument("--list-voices", action="store_true", help="List voices for the selected provider and exit")
    parser.add_argument("--list-samples", action="store_true", help="List samples for the selected provider and exit")
    parser.add_argument("--select", action="append", default=[], help="Regex filter for test code/title/section. Repeatable.")
    parser.add_argument("--skip-generated", action="store_true", help="Disable generated length sweep tests")
    parser.add_argument("--limit-sweep-start", type=int, default=350, help="Approx start chars for generated limit sweep")
    parser.add_argument("--limit-sweep-step", type=int, default=150, help="Approx char step for generated limit sweep")
    parser.add_argument("--limit-sweep-max", type=int, default=1700, help="Approx max chars for generated limit sweep")
    parser.add_argument("--max-tests", type=int, default=None, help="Optional cap after filtering")
    parser.add_argument("--dry-run", action="store_true", help="Parse tests and resolve voice only; do not call synthesis")
    parser.add_argument("--cache-mode", choices=["warm", "cold"], default="cold", help="cold adds a run tag into voice_context to bypass cache")
    parser.add_argument("--run-tag", default=None, help="Override cache bust tag / run grouping label")

    voice_group = parser.add_argument_group("voice selection")
    voice_group.add_argument("--voice-type", choices=["auto", "base", "reference", "description", "blend"], default="auto")
    voice_group.add_argument("--voice-id", help="Base voice_id for providers with named voices")
    voice_group.add_argument("--sample-id", help="Reference sample_id for providers with voice matching")
    voice_group.add_argument("--voice-description", help="Voice description for qwen_design-style providers")
    voice_group.add_argument("--blend", action="append", default=[], help="Blend entry voice_id:weight (repeatable)")
    voice_group.add_argument("--voice-context", default="provider-benchmark", help="Base voice_context prefix")
    voice_group.add_argument("--voice-instruct", default=None, help="Natural language style instruction")
    voice_group.add_argument("--cosy-instruct", default=None, help="CosyVoice style instruction")

    synth = parser.add_argument_group("synthesis controls")
    synth.add_argument("--lang-code", default="en")
    synth.add_argument("--speech-rate", type=float, default=1.0)
    synth.add_argument("--cfg-weight", type=float, default=None)
    synth.add_argument("--exaggeration", type=float, default=None)
    synth.add_argument("--cfg-strength", type=float, default=None)
    synth.add_argument("--nfe-step", type=int, default=None)
    synth.add_argument("--cross-fade-duration", type=float, default=None)
    synth.add_argument("--sway-sampling-coef", type=float, default=None)
    synth.add_argument("--lux-num-steps", type=int, default=None)
    synth.add_argument("--lux-t-shift", type=float, default=None)
    synth.add_argument("--lux-return-smooth", type=parse_bool, default=None)

    return parser.parse_args()


def parse_bool(value: str) -> bool:
    value = str(value).strip().lower()
    if value in {"1", "true", "yes", "y", "on"}:
        return True
    if value in {"0", "false", "no", "n", "off"}:
        return False
    raise argparse.ArgumentTypeError(f"Invalid boolean value: {value!r}")


def split_heading_code_and_title(heading: str, fallback_index: int) -> tuple[str, str]:
    heading = heading.strip()
    m = re.match(r"^([A-Za-z0-9]+(?:\.[0-9]+)?)\.\s+(.*)$", heading)
    if m:
        return m.group(1), m.group(2).strip()
    m = re.match(r"^(Sequence\s+[A-Za-z0-9]+)\s*:\s*(.*)$", heading, flags=re.IGNORECASE)
    if m:
        code = slugify(m.group(1)).replace("-", "_").upper()
        return code, m.group(2).strip() or m.group(1)
    code = f"T{fallback_index:03d}"
    return code, heading


def build_builtin_reference_tests() -> list[TestCase]:
    return [
        TestCase("1.A", "Short baseline", "1. Raw length stability tests", "This is a short baseline reading test. The goal is to hear whether the voice sounds natural, steady, and well paced from beginning to end. There are no unusual words, no tricky punctuation patterns, and no abrupt shifts in tone. If this sample sounds clean, then the model is stable on short narration.", "builtin", False),
        TestCase("1.B", "Medium baseline", "1. Raw length stability tests", "This is a medium length reading test designed to check whether the voice remains consistent over several sentences. The wording is simple and conversational. Each sentence is long enough to require some breath control, but not so long that a human speaker would struggle to keep the rhythm. Listen for sudden rushing, awkward pauses, flattened intonation, or signs that the model begins to lose track of sentence structure near the end.", "builtin", False),
        TestCase("1.C", "Long baseline", "1. Raw length stability tests", "This is a long reading test intended to reveal where the model begins to lose pacing, emphasis, or overall coherence. The passage uses straightforward vocabulary and mostly regular sentence structure so that any failure is more likely to come from sequence length than from difficult wording. A stable result should sound calm, evenly timed, and naturally phrased all the way through. A weak result may begin well and then slowly drift into rushed delivery, clipped endings, strange pauses, or changes in speaking rate that do not match the punctuation. If the voice starts to sound less certain as the passage continues, that usually suggests you are approaching the practical chunk limit for dependable narration.", "builtin", False),
        TestCase("1.D", "Extra-long baseline", "1. Raw length stability tests", "This is an extra long reading passage for stress testing. It is intentionally written in plain English so that pronunciation difficulty is not the main variable. The first sentence should sound just as stable as the last. The pacing should remain steady, with pauses landing naturally at commas and full stops. The pitch should not flatten, and the model should not begin to rush through phrases that deserve separation. The content itself is simple: a narrator is describing the purpose of a test, the expected result, and the kinds of errors that might appear when the sequence becomes too long. If the output begins to compress time, merge clauses together, skip emphasis, or insert pauses in unnatural places, that gives you a practical signal for where to split text into smaller units. If this sample still sounds good, then your safe chunk size is probably larger than you first assumed.", "builtin", False),
        TestCase("2.E", "Repeated sentence frame", "2. Repetition and pattern-drift tests", "I will read one simple sentence at a time. I will keep the same rhythm in every sentence. I will place emphasis only where it belongs. I will pause briefly at each period. I will continue with the same pacing throughout. I will avoid rushing the final words. I will maintain a natural voice from start to finish. I will keep the phrasing steady and clear.", "builtin", False),
        TestCase("2.F", "Repeated phrase with small changes", "2. Repetition and pattern-drift tests", "The first marker is quiet and clear. The second marker is calm and clear. The third marker is smooth and clear. The fourth marker is steady and clear. The fifth marker is warm and clear. The sixth marker is measured and clear. The seventh marker is balanced and clear. The eighth marker is natural and clear.", "builtin", False),
        TestCase("2.G", "Repetition fatigue test", "2. Repetition and pattern-drift tests", "Please read this line carefully and keep the same timing throughout. Please read this line carefully and keep the same timing throughout. Please read this line carefully and keep the same timing throughout. Please read this line carefully and keep the same timing throughout. Please read this line carefully and keep the same timing throughout. Please read this line carefully and keep the same timing throughout.", "builtin", False),
        TestCase("3.H", "Count by tens only", "3. Counting and ordered-sequence tests", "Ten, twenty, thirty, forty, fifty, sixty, seventy, eighty, ninety, one hundred.", "builtin", False),
        TestCase("3.I", "Count from one to twenty in words", "3. Counting and ordered-sequence tests", "one, two, three, four, five, six, seven, eight, nine, ten, eleven, twelve, thirteen, fourteen, fifteen, sixteen, seventeen, eighteen, nineteenth, twenty", "builtin", False),
        TestCase("3.J", "Count from twenty-one to forty in words", "3. Counting and ordered-sequence tests", "twenty-one, twenty-two, twenty-three, twenty-four, twenty-five, twenty-six, twenty-seven, twenty-eight, twenty-nine, thirty, thirty-one, thirty-two, thirty-three, thirty-four, thirty-five, thirty-six, thirty-seven, thirty-eight, thirty-nine, forty", "builtin", False),
        TestCase("3.K", "Count from forty-one to sixty in words", "3. Counting and ordered-sequence tests", "forty-one, forty-two, forty-three, forty-four, forty-five, forty-six, forty-seven, forty-eight, forty-nine, fifty, fifty-one, fifty-two, fifty-three, fifty-four, fifty-five, fifty-six, fifty-seven, fifty-eight, fifty-nine, sixty", "builtin", False),
        TestCase("3.L", "Ordinal sequence", "3. Counting and ordered-sequence tests", "first, second, third, fourth, fifth, sixth, seventh, eighth, ninth, tenth, eleventh, twelfth, thirteenth, fourteenth, fifteenth, sixteenth, seventeenth, eighteenth, nineteenth, twentieth", "builtin", False),
        TestCase("3.M", "Days and months sequence", "3. Counting and ordered-sequence tests", "Monday, Tuesday, Wednesday, Thursday, Friday, Saturday, Sunday. January, February, March, April, May, June, July, August, September, October, November, December.", "builtin", False),
        TestCase("4.N", "Short simple sentences", "4. Sentence and punctuation boundary tests", "The rain stopped. The street grew quiet. A door opened. Someone stepped outside. The air felt cool. A car passed slowly. Then everything settled again.", "builtin", False),
        TestCase("4.O", "Longer compound sentence flow", "4. Sentence and punctuation boundary tests", "The rain stopped, the street grew quiet, a door opened across the block, and someone stepped outside into the cool evening air. A car passed slowly, its tires soft against the pavement, and then the neighborhood settled back into silence.", "builtin", False),
        TestCase("4.P", "Semicolon and clause connection", "4. Sentence and punctuation boundary tests", "The system should pause at the major boundary; it should not break the sentence in the middle of a thought. It should also keep the second clause connected to the first; otherwise the pacing will sound artificial.", "builtin", False),
        TestCase("4.Q", "Colon list introduction", "4. Sentence and punctuation boundary tests", "There are three things to listen for: a stable speaking rate, natural pauses at punctuation, and consistent emphasis from beginning to end.", "builtin", False),
        TestCase("4.R", "Embedded clause stability", "4. Sentence and punctuation boundary tests", "The speaker, while sounding calm at first, may begin to rush later if the model is struggling to maintain control over a longer sequence.", "builtin", False),
        TestCase("4.S", "Quoted speech timing", "4. Sentence and punctuation boundary tests", 'She paused and said, "This is where the timing starts to slip," and then continued in a quieter voice.', "builtin", False),
        TestCase("5.T", "Paragraph as continuous prose", "5. Paragraph and line-break handling tests", "The test begins with a simple statement. It continues with a second sentence that has a similar rhythm. Then it adds a third sentence with slightly more detail so the model has to maintain pacing across a longer span. Finally, it ends with a sentence that should sound just as clear as the first.", "builtin", False),
        TestCase("5.U", "Paragraph with blank-line breaks", "5. Paragraph and line-break handling tests", """The test begins with a simple statement.

It continues with a second sentence that has a similar rhythm.

Then it adds a third sentence with slightly more detail so the model has to maintain pacing across a longer span.

Finally, it ends with a sentence that should sound just as clear as the first.
""", "builtin", False),
        TestCase("6.V", "Very short sequence", "6. Length ladder comparison tests", "We started early. The room was quiet. The lights were low. Everyone waited. Then the voice began.", "builtin", False),
        TestCase("6.W", "Medium compound sentence", "6. Length ladder comparison tests", "We started early, while the room was still quiet, and everyone waited for the first sentence to begin.", "builtin", False),
        TestCase("6.X", "Long compound sentence", "6. Length ladder comparison tests", "We started early, while the room was still quiet and the lights were low, and everyone waited with unusual patience for the first sentence to begin, because even a small change in timing would be easy to hear in that kind of silence.", "builtin", False),
        TestCase("7.Y1", "Long sentence without commas", "7. Punctuation sensitivity tests", "When the speaker continues through a long sentence without any punctuation the pacing must still sound stable and controlled from beginning to end.", "builtin", False),
        TestCase("7.Y2", "Long sentence with commas", "7. Punctuation sensitivity tests", "When the speaker continues through a long sentence, without any punctuation, the pacing must still sound stable and controlled, from beginning to end.", "builtin", False),
        TestCase("7.Z1", "Digits in list", "7. Punctuation sensitivity tests", "The package contains 12 parts, 4 adapters, 3 cables, and 2 printed guides.", "builtin", False),
        TestCase("7.Z2", "Number words in list", "7. Punctuation sensitivity tests", "The package contains twelve parts, four adapters, three cables, and two printed guides.", "builtin", False),
        TestCase("8.1", "Single segment baseline", "8. Segment accumulation tests", "This is test segment one. The voice should remain natural, evenly paced, and easy to follow.", "builtin", False),
        TestCase("8.2", "Two-segment accumulation", "8. Segment accumulation tests", "This is test segment one. The voice should remain natural, evenly paced, and easy to follow. This is test segment two. The same tone and timing should continue without noticeable drift.", "builtin", False),
        TestCase("8.3", "Three-segment accumulation", "8. Segment accumulation tests", "This is test segment one. The voice should remain natural, evenly paced, and easy to follow. This is test segment two. The same tone and timing should continue without noticeable drift. This is test segment three. Listen closely for any sign of rushing, flattening, or unstable pauses.", "builtin", False),
        TestCase("8.4", "Four-segment accumulation", "8. Segment accumulation tests", "This is test segment one. The voice should remain natural, evenly paced, and easy to follow. This is test segment two. The same tone and timing should continue without noticeable drift. This is test segment three. Listen closely for any sign of rushing, flattening, or unstable pauses. This is test segment four. If this still sounds clean, the current chunk size may be safe for ordinary narration.", "builtin", False),
        TestCase("9.A1", "Count block one to twenty", "9. Extended counting progression tests", "one, two, three, four, five, six, seven, eight, nine, ten, eleven, twelve, thirteen, fourteen, fifteen, sixteen, seventeen, eighteen, nineteen, twenty", "builtin", False),
        TestCase("9.A2", "Count block twenty-one to forty", "9. Extended counting progression tests", "twenty-one, twenty-two, twenty-three, twenty-four, twenty-five, twenty-six, twenty-seven, twenty-eight, twenty-nine, thirty, thirty-one, thirty-two, thirty-three, thirty-four, thirty-five, thirty-six, thirty-seven, thirty-eight, thirty-nine, forty", "builtin", False),
        TestCase("9.A3", "Count block forty-one to sixty", "9. Extended counting progression tests", "forty-one, forty-two, forty-three, forty-four, forty-five, forty-six, forty-seven, forty-eight, forty-nine, fifty, fifty-one, fifty-two, fifty-three, fifty-four, fifty-five, fifty-six, fifty-seven, fifty-eight, fifty-nine, sixty", "builtin", False),
        TestCase("9.B1", "Count slowly and clearly through thirty", "9. Extended counting progression tests", "Count slowly and clearly. One, two, three, four, five, six, seven, eight, nine, ten. Eleven, twelve, thirteen, fourteen, fifteen, sixteen, seventeen, eighteen, nineteen, twenty. Twenty-one, twenty-two, twenty-three, twenty-four, twenty-five, twenty-six, twenty-seven, twenty-eight, twenty-nine, thirty.", "builtin", False),
        TestCase("9.C1", "Counting with paragraph breaks", "9. Extended counting progression tests", """one, two, three, four, five, six, seven, eight, nine, ten

eleven, twelve, thirteen, fourteen, fifteen, sixteen, seventeen, eighteen, nineteen, twenty

twenty-one, twenty-two, twenty-three, twenty-four, twenty-five, twenty-six, twenty-seven, twenty-eight, twenty-nine, thirty

thirty-one, thirty-two, thirty-three, thirty-four, thirty-five, thirty-six, thirty-seven, thirty-eight, thirty-nine, forty""", "builtin", False),
    ]


def build_generated_limit_sweep(start: int, step: int, max_chars: int) -> list[TestCase]:
    sentence_pool = [
        "This passage is part of a length stability benchmark for narrated speech.",
        "The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty.",
        "A stable result should keep the same pacing, emphasis, and sentence order from beginning to end.",
        "If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit.",
        "This text continues in a calm descriptive style so duration and truncation can be compared against the reported input word count.",
    ]
    tests: list[TestCase] = []
    target = start
    counter = 1
    while target <= max_chars:
        text_parts: list[str] = []
        idx = 0
        while len(" ".join(text_parts)) < target:
            text_parts.append(sentence_pool[idx % len(sentence_pool)])
            idx += 1
        text = " ".join(text_parts)
        tests.append(
            TestCase(
                test_code=f"L{counter:03d}",
                title=f"Generated length sweep ~{target} chars",
                section="Generated length sweep",
                text=text,
                source="generated",
                generated=True,
            )
        )
        target += step
        counter += 1
    return tests


def count_sentences(text: str) -> int:
    chunks = re.split(r"(?<=[.!?])\s+|\n+", text.strip())
    return len([c for c in chunks if c.strip()])


def has_repeated_patterns(text: str) -> bool:
    normalized = re.sub(r"\s+", " ", text.lower()).strip()
    if not normalized:
        return False
    sentences = [s.strip() for s in re.split(r"(?<=[.!?])\s+", normalized) if s.strip()]
    if len(sentences) != len(set(sentences)):
        return True
    words = re.findall(r"[a-z']+", normalized)
    if not words:
        return False
    bigrams = [tuple(words[i:i+2]) for i in range(len(words) - 1)]
    return len(bigrams) != len(set(bigrams)) and len(bigrams) > 8


def has_number_words_or_ordered_terms(text: str) -> bool:
    tokens = {t.lower() for t in re.findall(r"[A-Za-z]+", text)}
    return any(tok in NUMBER_WORDS for tok in tokens)


def compute_text_metadata(text: str) -> dict[str, Any]:
    words = re.findall(r"\S+", text)
    return {
        "raw_text_chars": len(text),
        "raw_text_words": len(words),
        "sentence_count": count_sentences(text),
        "has_list_markers": bool(re.search(r"[,;:]", text) or "\n" in text),
        "has_repeated_patterns": has_repeated_patterns(text),
        "has_number_words_or_ordered_terms": has_number_words_or_ordered_terms(text),
        "has_digits": bool(re.search(r"\d", text)),
    }


def choose_voice_spec(args: argparse.Namespace, client: RrvBenchmarkClient, provider_info: dict[str, Any]) -> tuple[dict[str, Any], str]:
    provider_id = args.provider
    requested = args.voice_type

    if requested == "base":
        voice_id = args.voice_id
        if not voice_id:
            voices = client.get_voices(provider_id)
            if not voices:
                raise BenchmarkError(f"Provider '{provider_id}' has no base voices available")
            voice_id = voices[0]["voice_id"]
        return {"type": "base", "voice_id": voice_id}, f"base:{voice_id}"

    if requested == "reference":
        sample_id = args.sample_id
        if not sample_id:
            samples = client.get_samples(provider_id)
            if not samples:
                raise BenchmarkError(f"Provider '{provider_id}' has no reference samples available")
            sample_id = samples[0]["sample_id"]
        return {"type": "reference", "sample_id": sample_id}, f"reference:{sample_id}"

    if requested == "description":
        if not args.voice_description:
            raise BenchmarkError("--voice-description is required for --voice-type description")
        label = slugify(args.voice_description, max_len=32)
        return {"type": "description", "voice_description": args.voice_description}, f"description:{label}"

    if requested == "blend":
        entries = []
        for item in args.blend:
            try:
                voice_id, weight_text = item.split(":", 1)
                entries.append({"voice_id": voice_id.strip(), "weight": float(weight_text)})
            except ValueError as exc:
                raise BenchmarkError(f"Invalid --blend entry '{item}'. Expected voice_id:weight") from exc
        if not entries:
            raise BenchmarkError("At least one --blend voice_id:weight entry is required for --voice-type blend")
        label = "+".join(f"{e['voice_id']}@{e['weight']}" for e in entries)
        return {"type": "blend", "blend": entries}, f"blend:{label}"

    # auto mode
    if args.voice_id:
        return {"type": "base", "voice_id": args.voice_id}, f"base:{args.voice_id}"
    if args.sample_id:
        return {"type": "reference", "sample_id": args.sample_id}, f"reference:{args.sample_id}"
    if args.voice_description and provider_info.get("supports_voice_design"):
        label = slugify(args.voice_description, max_len=32)
        return {"type": "description", "voice_description": args.voice_description}, f"description:{label}"

    if provider_info.get("supports_base_voices"):
        voices = client.get_voices(provider_id)
        if voices:
            voice_id = voices[0]["voice_id"]
            return {"type": "base", "voice_id": voice_id}, f"base:{voice_id}"

    if provider_info.get("supports_voice_matching"):
        samples = client.get_samples(provider_id)
        if samples:
            sample_id = samples[0]["sample_id"]
            return {"type": "reference", "sample_id": sample_id}, f"reference:{sample_id}"

    if provider_info.get("supports_voice_design") and args.voice_description:
        label = slugify(args.voice_description, max_len=32)
        return {"type": "description", "voice_description": args.voice_description}, f"description:{label}"

    raise BenchmarkError(
        "Could not auto-select a voice. Supply one of --voice-id, --sample-id, or --voice-description "
        f"for provider '{provider_id}'."
    )


def build_payload(args: argparse.Namespace, provider_id: str, voice_spec: dict[str, Any], text: str, run_tag: str) -> dict[str, Any]:
    voice_context = args.voice_context or "provider-benchmark"
    if args.cache_mode == "cold":
        voice_context = f"{voice_context}|bench:{run_tag}"

    payload: dict[str, Any] = {
        "provider_id": provider_id,
        "text": text,
        "voice": voice_spec,
        "lang_code": args.lang_code,
        "speech_rate": args.speech_rate,
        "voice_context": voice_context,
    }
    optional_fields = {
        "cfg_weight": args.cfg_weight,
        "exaggeration": args.exaggeration,
        "cfg_strength": args.cfg_strength,
        "nfe_step": args.nfe_step,
        "cross_fade_duration": args.cross_fade_duration,
        "sway_sampling_coef": args.sway_sampling_coef,
        "voice_instruct": args.voice_instruct,
        "lux_num_steps": args.lux_num_steps,
        "lux_t_shift": args.lux_t_shift,
        "lux_return_smooth": args.lux_return_smooth,
        "cosy_instruct": args.cosy_instruct,
    }
    for key, value in optional_fields.items():
        if value is not None:
            payload[key] = value
    return payload


def sanitize_header_float(value: str | None) -> float | None:
    if value is None:
        return None
    value = value.strip()
    if not value:
        return None
    try:
        return float(value)
    except ValueError:
        return None


def sanitize_header_int(value: str | None) -> int | None:
    if value is None:
        return None
    value = value.strip()
    if not value:
        return None
    try:
        return int(value)
    except ValueError:
        return None


def select_tests(test_cases: list[TestCase], patterns: list[str], max_tests: int | None) -> list[TestCase]:
    selected = test_cases
    if patterns:
        regexes = [re.compile(p, flags=re.IGNORECASE) for p in patterns]
        selected = [
            tc for tc in selected
            if any(r.search(f"{tc.test_code} {tc.title} {tc.section}") for r in regexes)
        ]
    if max_tests is not None:
        selected = selected[:max_tests]
    return selected


def write_json(path: Path, data: Any) -> None:
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")


def write_csv(path: Path, results: list[RunResult]) -> None:
    rows = [dataclasses.asdict(r) for r in results]
    if not rows:
        path.write_text("", encoding="utf-8")
        return
    fieldnames = list(rows[0].keys())
    with path.open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)


def build_markdown_summary(args: argparse.Namespace, provider_info: dict[str, Any], voice_label: str, results: list[RunResult]) -> str:
    total = len(results)
    ok = sum(1 for r in results if r.status == "ok")
    errors = [r for r in results if r.status != "ok"]
    trunc = [r for r in results if r.likely_truncated is True]
    warm = [r for r in results if (r.header_cache or "").upper() == "HIT"]
    cold = [r for r in results if (r.header_cache or "").upper() == "MISS"]

    lines = []
    lines.append(f"# Benchmark Summary — {args.provider}")
    lines.append("")
    lines.append(f"- Server: `{args.server}`")
    lines.append(f"- Provider: `{args.provider}`")
    lines.append(f"- Voice: `{voice_label}`")
    lines.append(f"- Cache mode: `{args.cache_mode}`")
    lines.append(f"- Tests run: **{total}**")
    lines.append(f"- Successful results: **{ok}**")
    lines.append(f"- Errors: **{len(errors)}**")
    lines.append(f"- Likely truncation flags: **{len(trunc)}**")
    lines.append(f"- Cache hits: **{len(warm)}** | Cache misses: **{len(cold)}**")
    lines.append("")
    lines.append("## Provider capability snapshot")
    lines.append("")
    lines.append("```json")
    lines.append(json.dumps(provider_info, indent=2))
    lines.append("```")
    lines.append("")
    lines.append("## Results")
    lines.append("")
    lines.append("| # | Test | Section | Cache | Input chars | Input words | Duration | RTF | Expected dur | Ratio | Trunc? | File |")
    lines.append("|---:|---|---|---|---:|---:|---:|---:|---:|---:|---|---|")
    for r in results:
        lines.append(
            "| {idx} | {code} {title} | {section} | {cache} | {chars} | {words} | {dur} | {rtf} | {exp} | {ratio} | {trunc} | {file} |".format(
                idx=r.run_index,
                code=r.test_code,
                title=r.title.replace("|", "\\|"),
                section=r.section.replace("|", "\\|"),
                cache=(r.header_cache or "-") if r.status == "ok" else "ERR",
                chars=r.header_input_chars if r.header_input_chars is not None else "-",
                words=r.header_input_words if r.header_input_words is not None else "-",
                dur=f"{r.duration_sec:.3f}" if r.duration_sec is not None else "-",
                rtf=f"{r.realtime_factor:.3f}" if r.realtime_factor is not None else "-",
                exp=f"{r.expected_duration_sec:.3f}" if r.expected_duration_sec is not None else "-",
                ratio=f"{r.duration_ratio_vs_expected:.3f}" if r.duration_ratio_vs_expected is not None else "-",
                trunc="yes" if r.likely_truncated else "no",
                file=r.ogg_filename or (r.error or "-"),
            )
        )
    if errors:
        lines.append("")
        lines.append("## Errors")
        lines.append("")
        for r in errors:
            lines.append(f"- `{r.test_code} {r.title}` — {r.error}")
    return "\n".join(lines) + "\n"


def build_llm_packet(args: argparse.Namespace, voice_label: str, results: list[RunResult]) -> str:
    payload_rows = [dataclasses.asdict(r) for r in results]
    prompt = f"""# LLM Consolidation Packet — {args.provider}

Use this run to update your provider test notes for provider `{args.provider}`.

## Instructions for the LLM

1. Focus on provider `{args.provider}` only.
2. Use the measured API metrics as the primary source of truth for:
   - cache state
   - normalized input char/word counts
   - synth time
   - duration
   - realtime factor
3. Treat `likely_truncated=true` and low `duration_ratio_vs_expected` as evidence that the model may have truncated or compressed output.
4. Do **not** overwrite subjective listening conclusions unless the new metrics strongly contradict them.
5. Add or revise provider findings in your notes using concise test-by-test observations.
6. If the run used cache hits, do not draw throughput conclusions from those rows.
7. For generated `L###` rows, summarize where degradation appears to begin by approximate input chars/words and duration ratio.

## Run context

- Server: `{args.server}`
- Provider: `{args.provider}`
- Voice: `{voice_label}`
- Cache mode: `{args.cache_mode}`
- Language: `{args.lang_code}`
- Speech rate: `{args.speech_rate}`
- cfg_weight: `{args.cfg_weight}`
- exaggeration: `{args.exaggeration}`
- cfg_strength: `{args.cfg_strength}`
- nfe_step: `{args.nfe_step}`
- voice_instruct: `{args.voice_instruct}`
- cosy_instruct: `{args.cosy_instruct}`

## Structured results JSON

```json
{json.dumps(payload_rows, indent=2, ensure_ascii=False)}
```
"""
    return prompt


def print_json(data: Any) -> None:
    print(json.dumps(data, indent=2, ensure_ascii=False))


def main() -> int:
    args = parse_args()
    client = RrvBenchmarkClient(args.server, args.api_key, args.timeout)

    if args.list_providers:
        print_json(client.list_providers())
        return 0
    if args.list_voices:
        print_json(client.get_voices(args.provider))
        return 0
    if args.list_samples:
        print_json(client.get_samples(args.provider))
        return 0

    provider_info = client.get_provider(args.provider)
    voice_spec, voice_label = choose_voice_spec(args, client, provider_info)

    test_cases = build_builtin_reference_tests()
    if not args.skip_generated:
        test_cases.extend(build_generated_limit_sweep(args.limit_sweep_start, args.limit_sweep_step, args.limit_sweep_max))
    test_cases = select_tests(test_cases, args.select, args.max_tests)
    if not test_cases:
        raise SystemExit("No tests selected.")

    timestamp = dt.datetime.now().strftime("%Y%m%d_%H%M%S")
    run_tag = args.run_tag or f"{args.provider}_{timestamp}"
    outdir = Path(args.outdir) if args.outdir else Path.cwd() / "benchmark_runs" / f"{args.provider}_{timestamp}"
    outdir.mkdir(parents=True, exist_ok=True)
    audio_dir = outdir / "audio"
    audio_dir.mkdir(parents=True, exist_ok=True)

    manifest = {
        "created_at": dt.datetime.now().isoformat(),
        "server": args.server,
        "provider": args.provider,
        "voice": voice_spec,
        "voice_label": voice_label,
        "cache_mode": args.cache_mode,
        "run_tag": run_tag,
        "selected_tests": [{"test_code": t.test_code, "title": t.title, "generated": t.generated} for t in test_cases],
    }
    write_json(outdir / MANIFEST_JSON, manifest)

    print(f"Output directory: {outdir}")
    print(f"Provider: {args.provider}")
    print(f"Voice: {voice_label}")
    print(f"Tests: {len(test_cases)}")

    if args.dry_run:
        print("Dry run only. No synthesis requests sent.")
        return 0

    results: list[RunResult] = []

    for idx, test_case in enumerate(test_cases, start=1):
        metadata = compute_text_metadata(test_case.text)
        payload = build_payload(args, args.provider, voice_spec, test_case.text, f"{run_tag}_{test_case.test_code}")
        normalized_hash16 = safe_short_hash(test_case.text)
        print(f"[{idx}/{len(test_cases)}] {test_case.test_code} {test_case.title}")
        try:
            submit = client.submit_v2(payload)
            progress_key = submit.get("progress_key")
            if not progress_key:
                raise BenchmarkError(f"No progress_key returned for test {test_case.test_code}")
            submit_cached = submit.get("cached")
            progress_event = client.stream_progress_until_done(progress_key, args.progress_timeout)
            status = str(progress_event.get("status") or "").lower()
            if status == "error":
                raise BenchmarkError(str(progress_event.get("error") or f"Synthesis failed for {test_case.test_code}"))

            result_resp = client.get_result(progress_key)
            headers = result_resp.headers
            header_cache = headers.get("X-Cache")
            cache_key = headers.get("X-Cache-Key") or submit.get("cache_key")
            header_input_chars = sanitize_header_int(headers.get("X-Input-Chars"))
            header_input_words = sanitize_header_int(headers.get("X-Input-Words"))
            synth_time = sanitize_header_float(headers.get("X-Synth-Time"))
            duration = sanitize_header_float(headers.get("X-Duration"))
            rtf = sanitize_header_float(headers.get("X-Realtime-Factor"))
            expected_duration = None
            ratio = None
            likely_truncated = None
            if header_input_words is not None and duration is not None:
                expected_duration = (header_input_words / WPM_FOR_TRUNCATION_HEURISTIC) * 60.0
                ratio = duration / expected_duration if expected_duration > 0 else None
                likely_truncated = ratio is not None and ratio < 0.70

            filename = (
                f"{idx:03d}_{slugify(test_case.test_code, 16)}_"
                f"{slugify(test_case.title, 48)}__{args.provider}__"
                f"{slugify(voice_label, 40)}__chars-{header_input_chars or metadata['raw_text_chars']}"
                f"__words-{header_input_words or metadata['raw_text_words']}"
                f"__cache-{(header_cache or 'unknown').lower()}.ogg"
            )
            audio_path = audio_dir / filename
            audio_path.write_bytes(result_resp.content)

            result = RunResult(
                run_index=idx,
                test_code=test_case.test_code,
                title=test_case.title,
                section=test_case.section,
                generated=test_case.generated,
                provider_id=args.provider,
                server=args.server,
                voice_mode=voice_spec["type"],
                voice_label=voice_label,
                cache_mode=args.cache_mode,
                cache_bust_tag=run_tag,
                lang_code=args.lang_code,
                speech_rate=args.speech_rate,
                cfg_weight=args.cfg_weight,
                exaggeration=args.exaggeration,
                cfg_strength=args.cfg_strength,
                nfe_step=args.nfe_step,
                cross_fade_duration=args.cross_fade_duration,
                sway_sampling_coef=args.sway_sampling_coef,
                lux_num_steps=args.lux_num_steps,
                lux_t_shift=args.lux_t_shift,
                lux_return_smooth=args.lux_return_smooth,
                cosy_instruct=args.cosy_instruct,
                voice_instruct=args.voice_instruct,
                status="ok",
                submit_cached=submit_cached,
                header_cache=header_cache,
                cache_key=cache_key,
                progress_key=progress_key,
                input_chars_submit=submit.get("input_chars"),
                input_words_submit=submit.get("input_words"),
                header_input_chars=header_input_chars,
                header_input_words=header_input_words,
                synth_time_sec=synth_time,
                duration_sec=duration,
                realtime_factor=rtf,
                expected_duration_sec=expected_duration,
                duration_ratio_vs_expected=ratio,
                likely_truncated=likely_truncated,
                normalized_text_hash16=normalized_hash16,
                raw_text_chars=metadata["raw_text_chars"],
                raw_text_words=metadata["raw_text_words"],
                sentence_count=metadata["sentence_count"],
                has_list_markers=metadata["has_list_markers"],
                has_repeated_patterns=metadata["has_repeated_patterns"],
                has_number_words_or_ordered_terms=metadata["has_number_words_or_ordered_terms"],
                has_digits=metadata["has_digits"],
                ogg_filename=str(audio_path.relative_to(outdir)),
                result_url=f"{args.server}/api/v1/synthesize/v2/{progress_key}/result",
                error=None,
            )
            results.append(result)
            print(
                f"  -> cache={header_cache or submit_cached} chars={header_input_chars} words={header_input_words} "
                f"duration={duration} rtf={rtf} trunc={likely_truncated}"
            )
        except Exception as exc:
            result = RunResult(
                run_index=idx,
                test_code=test_case.test_code,
                title=test_case.title,
                section=test_case.section,
                generated=test_case.generated,
                provider_id=args.provider,
                server=args.server,
                voice_mode=voice_spec["type"],
                voice_label=voice_label,
                cache_mode=args.cache_mode,
                cache_bust_tag=run_tag,
                lang_code=args.lang_code,
                speech_rate=args.speech_rate,
                cfg_weight=args.cfg_weight,
                exaggeration=args.exaggeration,
                cfg_strength=args.cfg_strength,
                nfe_step=args.nfe_step,
                cross_fade_duration=args.cross_fade_duration,
                sway_sampling_coef=args.sway_sampling_coef,
                lux_num_steps=args.lux_num_steps,
                lux_t_shift=args.lux_t_shift,
                lux_return_smooth=args.lux_return_smooth,
                cosy_instruct=args.cosy_instruct,
                voice_instruct=args.voice_instruct,
                status="error",
                submit_cached=None,
                header_cache=None,
                cache_key=None,
                progress_key=None,
                input_chars_submit=None,
                input_words_submit=None,
                header_input_chars=None,
                header_input_words=None,
                synth_time_sec=None,
                duration_sec=None,
                realtime_factor=None,
                expected_duration_sec=None,
                duration_ratio_vs_expected=None,
                likely_truncated=None,
                normalized_text_hash16=normalized_hash16,
                raw_text_chars=metadata["raw_text_chars"],
                raw_text_words=metadata["raw_text_words"],
                sentence_count=metadata["sentence_count"],
                has_list_markers=metadata["has_list_markers"],
                has_repeated_patterns=metadata["has_repeated_patterns"],
                has_number_words_or_ordered_terms=metadata["has_number_words_or_ordered_terms"],
                has_digits=metadata["has_digits"],
                ogg_filename=None,
                result_url=None,
                error=str(exc),
            )
            results.append(result)
            print(f"  !! {exc}")

    write_json(outdir / RESULTS_JSON, [dataclasses.asdict(r) for r in results])
    write_csv(outdir / RESULTS_CSV, results)
    (outdir / RESULTS_MD).write_text(build_markdown_summary(args, provider_info, voice_label, results), encoding="utf-8")
    (outdir / LLM_PACKET_MD).write_text(build_llm_packet(args, voice_label, results), encoding="utf-8")

    print("\nWrote:")
    print(f"- {outdir / RESULTS_JSON}")
    print(f"- {outdir / RESULTS_CSV}")
    print(f"- {outdir / RESULTS_MD}")
    print(f"- {outdir / LLM_PACKET_MD}")
    print(f"- {audio_dir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
