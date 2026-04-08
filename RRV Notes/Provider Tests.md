Use a test matrix instead of one long prompt. That will tell you whether failures come from raw length, repeated structure, number words, punctuation, or prosody.

Below are paste-ready sequences grouped by what they test.

## How to use them

Run these in order and note:

- first point where pronunciation or pacing degrades
- whether the failure happens at commas, semicolons, paragraph breaks, or long lists
- whether it loses semantic order, breath rhythm, or sentence prosody
- whether breaking at sentence boundaries fixes it

Track for each sample:
- approximate character count
- word count
- number of sentences
- whether it contains lists
- whether it contains repeated patterns
- whether it contains number words

## 1) Raw length stability tests

These are plain prose with simple syntax. If these fail, your chunk size is mostly a length problem.

### A. Short baseline

```text
This is a short baseline reading test. The goal is to hear whether the voice sounds natural, steady, and well paced from beginning to end. There are no unusual words, no tricky punctuation patterns, and no abrupt shifts in tone. If this sample sounds clean, then the model is stable on short narration.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 497 - Pass
	Weight: 1, Exaggeration: 1, Tokens used: 497 - Pass
	Weight: 1, Exaggeration: 2, Tokens used: 466 - Pass  - Note: Voice is yelling.
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 302, words 53, duration 21.4s, ratio 0.875, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 560 - Pass

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good; flow is a little off and stretches some words. This may be sample-related.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 302, words 53, duration 26.0s, ratio 1.063, truncation no.

Findings F5
	Batches: 2 - Pass 

Finds Koroko
	Pass
### B. Medium baseline

```text
This is a medium length reading test designed to check whether the voice remains consistent over several sentences. The wording is simple and conversational. Each sentence is long enough to require some breath control, but not so long that a human speaker would struggle to keep the rhythm. Listen for sudden rushing, awkward pauses, flattened intonation, or signs that the model begins to lose track of sentence structure near the end.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 648 - Pass
	Weight: 1, Exaggeration: 1, Tokens used: 567 - Pass
	Weight: 1, Exaggeration: 2, Tokens used: 592 - Fail  - Note: Voice is yelling. Fails at the end.  says "near" then says "eh eh"
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 436, words 71, duration 27.1s, ratio 0.827, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 727 - Pass

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 436, words 71, duration 34.7s, ratio 1.059, truncation no.

Findings F5
	Batches: 2 - Pass

Findings Koroko
	Pass	
### C. Long baseline

```text
This is a long reading test intended to reveal where the model begins to lose pacing, emphasis, or overall coherence. The passage uses straightforward vocabulary and mostly regular sentence structure so that any failure is more likely to come from sequence length than from difficult wording. A stable result should sound calm, evenly timed, and naturally phrased all the way through. A weak result may begin well and then slowly drift into rushed delivery, clipped endings, strange pauses, or changes in speaking rate that do not match the punctuation. If the voice starts to sound less certain as the passage continues, that usually suggests you are approaching the practical chunk limit for dependable narration.
```
Findings
	Weight: 1, Exaggeration: 0.52, Tokens used: 1000+ - Fail - Note:  Stops at "chunk limit" some slurring on "P words"
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Fail, ended at "as the passage continues,". Metrics: chars 715, words 114, duration 40.0s, ratio 0.760, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 827 - Pass
	
Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; clipped after "the practical chunk limit".


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 715, words 114, duration 55.9s, ratio 1.062, truncation no.

Findings F5
	Batches: 4 - Pass - Some slight stutter on emphasis then fine.

Findings Koroko
	Pass	
## D. Extra-long baseline

```text
This is an extra long reading passage for stress testing. It is intentionally written in plain English so that pronunciation difficulty is not the main variable. The first sentence should sound just as stable as the last. The pacing should remain steady, with pauses landing naturally at commas and full stops. The pitch should not flatten, and the model should not begin to rush through phrases that deserve separation. The content itself is simple: a narrator is describing the purpose of a test, the expected result, and the kinds of errors that might appear when the sequence becomes too long. If the output begins to compress time, merge clauses together, skip emphasis, or insert pauses in unnatural places, that gives you a practical signal for where to split text into smaller units. If this sample still sounds good, then your safe chunk size is probably larger than you first assumed.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 1000+ - Fail - Note:  Stops at "unnatural places"
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Fail, ended at "skip emphasis, or insert pauses". Metrics: chars 894, words 150, duration 40.0s, ratio 0.578, truncation yes.


Findings Chatterbox-Turbo
	Tokens Used: 872 - Failed at end  garbled
	
Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; clipped after "text into smaller unit" then continued in a different tone with "If this sample still sounds good, then your safe chunk size is probably larger than you first assumed."


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 894, words 150, duration 73.4s, ratio 1.060, truncation no.

Findings F5
	Batches: 4 - Pass  

Findings Koroko
	Pass	
	
## 2) Repetition and pattern-drift tests

These find whether the model loses order when structure repeats.

### E. Repeated sentence frame

```text
I will read one simple sentence at a time. I will keep the same rhythm in every sentence. I will place emphasis only where it belongs. I will pause briefly at each period. I will continue with the same pacing throughout. I will avoid rushing the final words. I will maintain a natural voice from start to finish. I will keep the phrasing steady and clear.
```
Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 552 - Pass 
	Weight: 1, Exaggeration: 1, Tokens used: 494 - Pass
	Weight: 1, Exaggeration: 2, Tokens used: 493 - Pass
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 355, words 66, duration 23.5s, ratio 0.771, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 592 - Pass

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 355, words 66, duration 32.3s, ratio 1.060, truncation no.

Findings F5
	Batches: 2 - Pass 

Findings Koroko
	Pass	
	
### F. Repeated phrase with small changes

```text
The first marker is quiet and clear. The second marker is calm and clear. The third marker is smooth and clear. The fourth marker is steady and clear. The fifth marker is warm and clear. The sixth marker is measured and clear. The seventh marker is balanced and clear. The eighth marker is natural and clear.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 661 - Fail - Note: Replaced  "The seventh marker" with "The eighth marker" then said "The eighth marker is natural and clear" correctly
	Weight: 1, Exaggeration: 1, Tokens used: 481 - Pass
	Weight: 1, Exaggeration: 2, Tokens used: 439 - Fail - Note: Ended at "eighth marker" then said "neh"
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 308, words 56, duration 19.8s, ratio 0.766, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 573 - Pass

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good; weird pronunciation of "fourth" and "sixth". Likely sample-related.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 308, words 56, duration 27.4s, ratio 1.060, truncation no.

Findings F5
	Batches: 2 - Pass 

Findings Koroko
	Pass	- All sentences said the same.
### G. Repetition fatigue test

```text
Please read this line carefully and keep the same timing throughout. Please read this line carefully and keep the same timing throughout. Please read this line carefully and keep the same timing throughout. Please read this line carefully and keep the same timing throughout. Please read this line carefully and keep the same timing throughout. Please read this line carefully and keep the same timing throughout.
```

If this fails early, the model may dislike repeated-token patterns even when it still has plenty of context left.

Findings Chatterbox
* Weight: 1, Exaggeration: 0.52, Tokens used: 568 - Pass 
* Weight: 1, Exaggeration: 1, Tokens used: 529 - Pass
* Weight: 1, Exaggeration: 2, Tokens used: 545 - Pass
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 413, words 66, duration 30.0s, ratio 0.985, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 739 - Pass

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; read "Please read this line carefully and keep the same timing throughout." two extra times.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 413, words 66, duration 32.3s, ratio 1.060, truncation no.

Findings F5
	Batches: 2 - Fail - changes the pronunciation of "read" randomly  

Findings Koroko
	Pass	
## 3) Counting and ordered-sequence tests

These isolate the exact kind of failure you already saw.

### H. Count by tens only

```text
Ten, twenty, thirty, forty, fifty, sixty, seventy, eighty, ninety, one hundred.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 184 - Pass 
	Weight: 1, Exaggeration: 1, Tokens used: 130 - Pass
	Weight: 1, Exaggeration: 2, Tokens used: 141 - Fail - Missed words, garbled end
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 79, words 11, duration 7.1s, ratio 1.398, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 169 - Pass
	
Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good, but progressively slowed while speaking. Likely sample-related.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 79, words 11, duration 5.3s, ratio 1.044, truncation no.

Findings F5
	Batches: 1 - pass  

Findings Koroko
	Pass	

### I. Count from one to twenty in words

```text
one, two, three, four, five, six, seven, eight, nine, ten, eleven, twelve, thirteen, fourteen, fifteen, sixteen, seventeen, eighteen, nineteen, twenty
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 305 - Pass 
	Weight: 1, Exaggeration: 1, Tokens used: 288 - Pass
	Weight: 1, Exaggeration: 2, Tokens used: 245 - Fail - Mixed up words
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 150, words 20, duration 10.7s, ratio 1.159, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 364 - Fail - Repeated words

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; said "nineteen" as "nineteenth."


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Pass - Said ninteen as nineteenth. Metrics: chars 152, words 20, duration 9.9s, ratio 1.073, truncation no.

Findings F5
	Batches: 1 - pass  

Findings Koroko
	Pass	
### J. Count from twenty-one to forty in words

```text
twenty-one, twenty-two, twenty-three, twenty-four, twenty-five, twenty-six, twenty-seven, twenty-eight, twenty-nine, thirty, thirty-one, thirty-two, thirty-three, thirty-four, thirty-five, thirty-six, thirty-seven, thirty-eight, thirty-nine, forty
```


Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 555 - Fail - Mixed up words.   "Thirty-Three" said Twice and skipped words 
	Weight: 1, Exaggeration: 1, Tokens used: 425 - Fail - Mixed up words
	Weight: 1, Exaggeration: 2, Tokens used: 370 - Fail - Mixed up words
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Fail, hallucinated after "thirty-nine, forty"; said "thirty-nine, forty-six" and more. Metrics: chars 247, words 20, duration 19.0s, ratio 2.058, truncation no.
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Fail - after "thirty-one," random output after. Metrics: chars 247, words 20, duration 19.4s, ratio 2.102, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 558 - Fail - Ended with "fourty two"

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Fail - cut off fouty. Metrics: chars 247, words 20, duration 18.8s, ratio 2.037, truncation no.

Findings F5
	Batches: 2 - pass  - says it right but sound Stilted twenty-one says then as seperate distinct works as well as the the others

Findings Koroko
	Pass	

### K. Count from forty-one to sixty in words

```text
forty-one, forty-two, forty-three, forty-four, forty-five, forty-six, forty-seven, forty-eight, forty-nine, fifty, fifty-one, fifty-two, fifty-three, fifty-four, fifty-five, fifty-six, fifty-seven, fifty-eight, fifty-nine, sixty
```

This split helps you see whether the failure is tied to total length or to a particular region in the count sequence.

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 380 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 368 - Pass
	Weight: 1, Exaggeration: 2, Tokens used: 370 - Fail - Repeated words
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Fail, hallucinated after "forty-nine, fifty,"; random variants after. Metrics: chars 228, words 20, duration 19.3s, ratio 2.091, truncation no.
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Fail - after "forty-nine, fifty," random output after. Metrics: chars 228, words 20, duration 20.4s, ratio 2.210, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 490 - Pass 
	
Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; started at "fourty-two."


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Fail - missed forty-thee. Metrics: chars 228, words 20, duration 18.8s, ratio 2.037, truncation no.

Findings F5
	Batches: 2 - pass  - says it right but sound Stilted un natural

Findings Koroko
	Pass	
### L. Ordinal sequence

```text
first, second, third, fourth, fifth, sixth, seventh, eighth, ninth, tenth, eleventh, twelfth, thirteenth, fourteenth, fifteenth, sixteenth, seventeenth, eighteenth, nineteenth, twentieth
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 282 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 237 - Pass
	Weight: 1, Exaggeration: 2, Tokens used: 300 - Fail - Repeated words
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 186, words 20, duration 18.5s, ratio 2.004, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 494 - Fail - Ended in "Dongtieth" 

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 186, words 20, duration 9.9s, ratio 1.073, truncation no.

Findings F5
	Batches: 1 - pass 

Findings Koroko
	Pass	

### M. Days and months sequence

```text
Monday, Tuesday, Wednesday, Thursday, Friday, Saturday, Sunday. January, February, March, April, May, June, July, August, September, October, November, December.
```

If these drift, the issue may be ordered-list stability rather than number words specifically.

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 297 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 349 - Pass
	Weight: 1, Exaggeration: 2, Tokens used: 313 - Fail - Repeated words and garbled
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 161, words 19, duration 11.6s, ratio 1.323, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 302 - Pass 

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 161, words 19, duration 9.3s, ratio 1.061, truncation no.

Findings F5
	Batches: 1 - pass  
	
Findings Koroko
	Pass	
## 4) Pacing and punctuation tests

These help you figure out where to split text for natural speech.

### N. Short sentences only

```text
The rain stopped. The street grew quiet. A door opened. Someone stepped outside. The air felt cool. A car passed slowly. Then everything settled again.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 279 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 239 - Pass
	Weight: 1, Exaggeration: 2, Tokens used: 269 - Fail - Repeated words
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 151, words 25, duration 11.4s, ratio 0.988, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 298 - Pass 

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 151, words 25, duration 12.1s, ratio 1.049, truncation no.

Findings F5
	Batches: 1 - pass  

Findings Koroko
	Pass	
	
### O. Long sentences with commas

```text
The rain stopped, the street grew quiet, a door opened across the block, and someone stepped outside into the cool evening air. A car passed slowly, its tires soft against the pavement, and then the neighborhood settled back into silence.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 373 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 307 - Pass
	Weight: 1, Exaggeration: 2, Tokens used: 321 - Fail - stutter.  ended with "I"
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 238, words 40, duration 16.6s, ratio 0.899, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 445 - Pass 

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 238, words 40, duration 19.7s, ratio 1.067, truncation no.

Findings F5
	Batches: 1 - pass  

Findings Koroko
	Pass	
	
### P. Semicolon and clause test

```text
The system should pause at the major boundary; it should not break the sentence in the middle of a thought. It should also keep the second clause connected to the first; otherwise the pacing will sound artificial.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 313 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 270 - Pass
	Weight: 1, Exaggeration: 2, Tokens used: 318 - Fail - ended with "WHY?"
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 213, words 37, duration 13.7s, ratio 0.802, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 304 - Pass 

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 213, words 37, duration 18.1s, ratio 1.060, truncation no.

Findings F5
	Batches: 1 - pass  
	
Findings Koroko
	Pass	
	
### Q. Colon and list intro

```text
There are three things to listen for: a stable speaking rate, natural pauses at punctuation, and consistent emphasis from beginning to end.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 230 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 217 - Pass
	Weight: 1, Exaggeration: 2, Tokens used: 197 - Pass
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 139, words 22, duration 10.8s, ratio 1.064, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 249 - Pass 

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 139, words 22, duration 10.6s, ratio 1.044, truncation no.

Findings F5
	Batches: 1 - pass  

Findings Koroko
	Pass	
### R. Parenthetical interruption

```text
The speaker, while sounding calm at first, may begin to rush later if the model is struggling to maintain control over a longer sequence.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 221 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 204 - Pass
	Weight: 1, Exaggeration: 2, Tokens used: 208 - Pass
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 137, words 24, duration 8.4s, ratio 0.758, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 226 - Pass 

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 137, words 24, duration 11.6s, ratio 1.047, truncation no.

Findings F5
	Batches: 1 - pass  

Findings Koroko
	Pass	
### S. Quotation marks

```text
She paused and said, "This is where the timing starts to slip," and then continued in a quieter voice.
```

If commas work but semicolons or parentheses fail, split on those marks before inference.

Findings Chatterbox
* Weight: 1, Exaggeration: 0.52, Tokens used: 173 - Pass  - ignored pause in quotes
* Weight: 1, Exaggeration: 1, Tokens used: 169 - Pass - really emphasized text in quotes
* Weight: 1, Exaggeration: 2, Tokens used: 173 - Fail - Really emphasized text in quotes but cotinued yetting the rest. and garbled the end.
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 102, words 19, duration 6.9s, ratio 0.787, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 154 - Pass 

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 102, words 19, duration 9.3s, ratio 1.061, truncation no.

Findings F5
	Batches: 1 - pass  

Findings Koroko
	Pass	
	
## 5) Paragraph-boundary tests

These tell you whether line breaks help.

### T. Same content, one paragraph

```text
The test begins with a simple statement. It continues with a second sentence that has a similar rhythm. Then it adds a third sentence with slightly more detail so the model has to maintain pacing across a longer span. Finally, it ends with a sentence that should sound just as clear as the first.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 421 - Pass 
	Weight: 1, Exaggeration: 1, Tokens used: 399 - Pass 
	Weight: 1, Exaggeration: 2, Tokens used: 420 - Pass
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 296, words 54, duration 18.2s, ratio 0.730, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 503 - Pass 

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 296, words 54, duration 26.5s, ratio 1.063, truncation no.

Findings F5
	Batches: 2 - pass  
	
Findings Koroko
	Pass	
### U. Same content, broken into lines

```text
The test begins with a simple statement.

It continues with a second sentence that has a similar rhythm.

Then it adds a third sentence with slightly more detail so the model has to maintain pacing across a longer span.

Finally, it ends with a sentence that should sound just as clear as the first.
```

If U works better than T, paragraph or line break chunking is probably useful.

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 439 - Pass - Sound much more natural than 5T
	Weight: 1, Exaggeration: 1, Tokens used: 411 - Pass - Sounds much more natural than 5T
	Weight: 1, Exaggeration: 2, Tokens used: 445 - Pass
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good; better pacing. Metrics: chars 299, words 54, duration 19.3s, ratio 0.774, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 467 - Pass - Sounds much more natural than 5T

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 299, words 54, duration 26.5s, ratio 1.063, truncation no.

Findings F5
	Batches: 2 - pass  - Sounded very good.

Findings Koroko
	Pass	- Sounds better than 5T

## 6) Breath-load tests

These reveal when sentence length itself causes prosody collapse.

### V. Very short breath groups

```text
We started early. The room was quiet. The lights were low. Everyone waited. Then the voice began.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 171 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 159 - Pass 
	Weight: 1, Exaggeration: 2, Tokens used: 158 - Fail
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 97, words 17, duration 7.7s, ratio 0.981, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 213 - Pass 

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 97, words 17, duration 8.3s, ratio 1.058, truncation no.

Findings F5
	Batches: 1 - pass  

Findings Koroko
	Pass	
### W. Medium breath groups

```text
We started early, while the room was still quiet, and everyone waited for the first sentence to begin.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used: 153 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 133 - Pass 
	Weight: 1, Exaggeration: 2, Tokens used: 129 - Pass
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 102, words 18, duration 6.0s, ratio 0.722, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 165 - Pass 

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 102, words 18, duration 8.8s, ratio 1.059, truncation no.

Findings F5
	Batches: 1 - pass  
	
Findings Koroko
	Pass	
### X. Heavy breath group

```text
We started early, while the room was still quiet and the lights were low, and everyone waited with unusual patience for the first sentence to begin, because even a small change in timing would be easy to hear in that kind of silence.
```

If V and W are fine but X collapses, split long sentences even if total chunk size is still safe.

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used:  340 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 335 - Pass 
	Weight: 1, Exaggeration: 2, Tokens used: 355 - Fail - Repeated words
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 233, words 43, duration 13.9s, ratio 0.700, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 371 - Pass 

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good; slightly consistent pausing, very artificial.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Pass - long pause on "begin, because". Metrics: chars 233, words 43, duration 20.9s, ratio 1.053, truncation no.

Findings F5
	Batches: 1 - pass  

Findings Koroko
	Pass	
## 7) Near-minimal pair tests for pacing

These are useful because only one variable changes.

### Y1. No commas

```text
When the speaker continues through a long sentence without any punctuation the pacing must still sound stable and controlled from beginning to end.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used:  221 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 232 - Pass - Sounded hurried at the end.
	Weight: 1, Exaggeration: 2, Tokens used: 225 - Pass
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 147, words 23, duration 9.7s, ratio 0.914, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 208 - Pass - Slightly off at the end. tone stayed flat didn't go down like expected.

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; slowed throughout the sequence and felt too slow by the end.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 147, words 23, duration 11.1s, ratio 1.046, truncation no.

Findings F5
	Batches: 1 - pass - Slightly off unsettling at the start.

Findings Koroko
	Pass	
	
### Y2. With commas

```text
When the speaker continues through a long sentence, without any punctuation, the pacing must still sound stable and controlled, from beginning to end.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used:  246 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 250 - Pass 
	Weight: 1, Exaggeration: 2, Tokens used: 230 - Fail - Ended with extra word "when!"
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 150, words 23, duration 10.3s, ratio 0.970, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 233 - Pass 

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 150, words 23, duration 11.1s, ratio 1.046, truncation no.

Findings F5
	Batches: 1 - pass 
	
Findings Koroko
	Pass	
  
### Z1. Digits

```text
The package contains 12 parts, 4 adapters, 3 cables, and 2 printed guides.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used:  158 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 173 - Pass 
	Weight: 1, Exaggeration: 2, Tokens used: 155 - Fail - Garbled
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 94, words 17, duration 6.5s, ratio 0.828, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 151 - Pass 

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 94, words 17, duration 6.4s, ratio 0.816, truncation no.

Findings F5
	Batches: 1 - pass 

Findings Koroko
	Pass	
### Z2. Number words

```text
The package contains twelve parts, four adapters, three cables, and two printed guides.
```

If digits work but number words fail, normalize numbers differently before TTS.

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used:  154 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 166 - Pass 
	Weight: 1, Exaggeration: 2, Tokens used: 156 - Fail - Repeated words
  
  Findings Chatterbox-Turbo
	Tokens Used: 167 - Pass
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 87, words 13, duration 6.7s, ratio 1.117, truncation no.


Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good, but sounded stilted.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 87, words 13, duration 6.4s, ratio 1.067, truncation no.

Findings F5
	Batches: 1 - pass 

Findings Koroko
	Pass	
	
## 8) Safe chunk boundary finder

Use this staged test to find a practical maximum.

Paste these one at a time.

### Stage 1

```text
This is test segment one. The voice should remain natural, evenly paced, and easy to follow.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used:  165 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 152 - Pass 
	Weight: 1, Exaggeration: 2, Tokens used: 177 - Fail
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 92, words 16, duration 6.9s, ratio 0.934, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 155 - Pass 

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 92, words 16, duration 7.8s, ratio 1.056, truncation no.

Findings F5
	Batches: 1 - pass 

Findings Koroko
	Pass	
	
### Stage 2

```text
This is test segment one. The voice should remain natural, evenly paced, and easy to follow. This is test segment two. The same tone and timing should continue without noticeable drift.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used:  347 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 309 - Pass 
	Weight: 1, Exaggeration: 2, Tokens used: 303 - Fail
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 185, words 31, duration 12.7s, ratio 0.888, truncation no.


Finding Chatterbox-Turbo
	Tokens Used: 314 - Pass 

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Pass, but the voice got more dramatic as it went on.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 185, words 31, duration 15.1s, ratio 1.055, truncation no.

Findings F5
	Batches: 1 - pass 

Findings Koroko
	Pass	
### Stage 3

```text
This is test segment one. The voice should remain natural, evenly paced, and easy to follow. This is test segment two. The same tone and timing should continue without noticeable drift. This is test segment three. Listen closely for any sign of rushing, flattening, or unstable pauses.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used:  502 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 462 - Pass 
	Weight: 1, Exaggeration: 2, Tokens used: 439 - Fail - Repeated words
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 285, words 47, duration 22.1s, ratio 1.019, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 598 - Pass 

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 285, words 47, duration 23.0s, ratio 1.060, truncation no.

Findings F5
	Batches: 2 - pass 

Findings Koroko
	Pass	
### Stage 4

```text
This is test segment one. The voice should remain natural, evenly paced, and easy to follow. This is test segment two. The same tone and timing should continue without noticeable drift. This is test segment three. Listen closely for any sign of rushing, flattening, or unstable pauses. This is test segment four. If this still sounds clean, the current chunk size may be safe for ordinary narration.
```

Keep extending by one sentence until quality drops. That gives you a practical sentence-count limit.

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used:  726 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 453 - Pass
	Weight: 1, Exaggeration: 2, Tokens used: 590 - Pass
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 399, words 67, duration 28.6s, ratio 0.925, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 875 - Fail  - Garbled last sentence 

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; segment 1 and 2 rushed, segment 3 and 4 were good.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 399, words 67, duration 32.8s, ratio 1.061, truncation no.

Findings F5
	Batches: 2 - pass 

Findings Koroko
	Pass	- Said correctly,  but missing natural breath pauses.
## 9) Best stress test for your exact issue

Since counting in whole words broke around thirty, use these three directly:

### Sequence A1:

```text
one, two, three, four, five, six, seven, eight, nine, ten, eleven, twelve, thirteen, fourteen, fifteen, sixteen, seventeen, eighteen, nineteen, twenty
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used:  273 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 203 - Pass
	Weight: 1, Exaggeration: 2, Tokens used: 223 - Fail - Repeated words 

Findings Chatterbox-Turbo
	Tokens Used: 331 - Fail - Said "Once" instead of "one" 

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; stopped after nineteen.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Fail - missed two I think I need to expand chars from 20 to 30 or 50. Metrics: chars 150, words 20, duration 9.9s, ratio 1.073, truncation no.

Findings F5
	Batches: 2 - pass 
	
Findings Koroko
	Pass	
	
### Sequence A2:	
```text
twenty-one, twenty-two, twenty-three, twenty-four, twenty-five, twenty-six, twenty-seven, twenty-eight, twenty-nine, thirty, thirty-one, thirty-two, thirty-three, thirty-four, thirty-five, thirty-six, thirty-seven, thirty-eight, thirty-nine, forty
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used:  410 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 515 - Fail - Repeated words
	Weight: 1, Exaggeration: 2, Tokens used: 223 - Fail - Repeated words 
  
Findings Chatterbox-Turbo
	Tokens Used: 331 - Fail - Repeated words

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; huge pausing between counts, clipped at "Thirty-Eight," then continued at forty.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Pass - Clipped forty but think that is my implemetation. Metrics: chars 247, words 20, duration 18.8s, ratio 2.037, truncation no.

Findings F5
	Batches: 2 - pass - Stilted.  Say words stilted.
	
Findings Koroko
	Pass	

### Sequence A3:
```text
forty-one, forty-two, forty-three, forty-four, forty-five, forty-six, forty-seven, forty-eight, forty-nine, fifty, fifty-one, fifty-two, fifty-three, fifty-four, fifty-five, fifty-six, fifty-seven, fifty-eight, fifty-nine, sixty
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used:  417 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 360 - Pass
	Weight: 1, Exaggeration: 2, Tokens used: 371 - Fail - Repeated words 

Findings Chatterbox-Turbo
	Tokens Used: 528 - Pass

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; said "forty-own."


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Fail - missed said fourty-two as fourty-too then got ramdom almost recentered and lost it again. Metrics: chars 228, words 20, duration 18.8s, ratio 2.037, truncation no.

Findings F5
	Batches: 2 - pass - Stilted.  Say words stilted.

Findings Koroko
	Pass	

### Sequence B1: same count, but sentence grouping

```text
Count slowly and clearly. One, two, three, four, five, six, seven, eight, nine, ten. Eleven, twelve, thirteen, fourteen, fifteen, sixteen, seventeen, eighteen, nineteen, twenty. Twenty-one, twenty-two, twenty-three, twenty-four, twenty-five, twenty-six, twenty-seven, twenty-eight, twenty-nine, thirty.
```

Findings Chatterbox
	Weight: 1, Exaggeration: 0.52, Tokens used:  531 - Pass  
	Weight: 1, Exaggeration: 1, Tokens used: 550 - Fail - Swapped words. 
	Weight: 1, Exaggeration: 2, Tokens used: 488 - Fail - Repeated words
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 302, words 34, duration 22.7s, ratio 1.447, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 574 - Fail - ended in "Thirty-two"

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; stopped after "twenty."


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Fail - It didn't say "Count slowy and clearly."  started at one . failed on nineteen, just said nineteen. Metrics: chars 302, words 34, duration 21.1s, ratio 1.345, truncation no.

Findings F5
	Batches: 2 - pass 

Findings Koroko
	Pass	- Did not follow the instruction at the start.
### Sequence C1: same count, but line grouping

```text
one, two, three, four, five, six, seven, eight, nine, ten

eleven, twelve, thirteen, fourteen, fifteen, sixteen, seventeen, eighteen, nineteen, twenty

twenty-one, twenty-two, twenty-three, twenty-four, twenty-five, twenty-six, twenty-seven, twenty-eight, twenty-nine, thirty

thirty-one, thirty-two, thirty-three, thirty-four, thirty-five, thirty-six, thirty-seven, thirty-eight, thirty-nine, forty
```

That will tell you whether the failure is:

- sequence-memory drift
- list-length drift
- comma-chain overload
- lack of sentence boundaries
- lack of paragraph boundaries
    
Findings
* Weight: 1, Exaggeration: 0.3, Tokens used:  1000+ - Fail  - Token overflow, repeated words
* Weight: 1, Exaggeration: 0.5, Tokens used:  628 - Fail  - Repeated Words
* Weight: 1, Exaggeration: 0.52, Tokens used:  562 - Fail  - at end started repeating twenty
* Weight: 1, Exaggeration: 1, Tokens used: 476 - Fail - forty cut off abruptly.  
* Weight: 1, Exaggeration: 2, Tokens used: 485 - Fail - Repeated words
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Fail - after "twenty-four, twenty-five," word swapping after. Metrics: chars 399, words 40, duration 21.3s, ratio 1.154, truncation no.


Findings Chatterbox-Turbo
	Tokens Used: 820 - Fail - Repeated words, garbled at end

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; stopped after "thirty," but good until then.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Fail - said "One, Two, Six, Seven, Eight, Nine Ten"  random failures after. Metrics: chars 399, words 40, duration 28.4s, ratio 1.538, truncation no.

Findings F5
	Batches: 2 - pass - Stilted after twenty
	
Findings Koroko
	Pass	- No breath pausing
## 10) Generated limit sweep tests (L001-L010)

These are the exact generated length-sweep prompts embedded in `rrv_benchmark.py`. They should remain aligned with the script so benchmark runs and manual review use the same text.

The purpose of these tests is to step sequence length upward in a controlled way while keeping wording simple and repetitive enough to expose truncation, pacing drift, clipping, repeated fragments, or loss of sentence order.

### L001. Generated length sweep ~350 chars

```text
This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit.
```

Findings
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 409, words 64, duration 27.0s, ratio 0.914, truncation no.

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; clipped after "or lose structure."


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 409, words 64, duration 31.2s, ratio 1.056, truncation no.

### L002. Generated length sweep ~500 chars

```text
This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit. This text continues in a calm descriptive style so duration and truncation can be compared against the reported input word count.
```

Findings
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Good. Metrics: chars 539, words 85, duration 34.9s, ratio 0.890, truncation no.

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; skipped "that usually marks the practical chunk limit." Then continued with "This text continues in a calm descriptive style so duration and truncation can be compared against the reported input word count." and ended with "re."


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Fail - Ended on "input word count". Metrics: chars 539, words 85, duration 41.6s, ratio 1.060, truncation no.

### L003. Generated length sweep ~650 chars

```text
This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit. This text continues in a calm descriptive style so duration and truncation can be compared against the reported input word count. This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty.
```

Findings
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Fail. Metrics: chars 726, words 113, duration 40.0s, ratio 0.767, truncation no.

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; said "word count" with extra pausing between "word" and "count," and the rest had odd pausing.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Fail - Ended on "than pronunciation difficulty". Metrics: chars 726, words 113, duration 55.4s, ratio 1.062, truncation no.

### L004. Generated length sweep ~800 chars

```text
This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit. This text continues in a calm descriptive style so duration and truncation can be compared against the reported input word count. This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end.
```

Findings
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Fail. Metrics: chars 823, words 129, duration 40.0s, ratio 0.672, truncation yes.

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; skipped "that usually marks the practical chunk limit." then continued as normal.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Fail - Ended on "from begining to end". Metrics: chars 823, words 129, duration 63.1s, ratio 1.060, truncation no.

### L005. Generated length sweep ~950 chars

```text
This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit. This text continues in a calm descriptive style so duration and truncation can be compared against the reported input word count. This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit. This text continues in a calm descriptive style so duration and truncation can be compared against the reported input word count.
```

Findings
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Fail. Metrics: chars 1079, words 170, duration 40.0s, ratio 0.510, truncation yes.

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; did not pause after the period in "pronunciation difficulty. A stable."


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Fail - Ended on "reported input word count.". Metrics: chars 1079, words 170, duration 83.3s, ratio 1.062, truncation no.

### L006. Generated length sweep ~1100 chars

```text
This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit. This text continues in a calm descriptive style so duration and truncation can be compared against the reported input word count. This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit. This text continues in a calm descriptive style so duration and truncation can be compared against the reported input word count. This passage is part of a length stability benchmark for narrated speech.
```

Findings
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Fail. Metrics: chars 1153, words 182, duration 40.0s, ratio 0.476, truncation yes.

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; skipped "pronunciation difficulty. A stable" and later skipped ", and sentence order from beginning to end." then continued.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Fail - Ended on " benchmark for narrat". Metrics: chars 1153, words 182, duration 89.0s, ratio 1.060, truncation no.

### L007. Generated length sweep ~1250 chars

```text
This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit. This text continues in a calm descriptive style so duration and truncation can be compared against the reported input word count. This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit. This text continues in a calm descriptive style so duration and truncation can be compared against the reported input word count. This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty.
```

Findings
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Fail. Metrics: chars 1266, words 198, duration 40.0s, ratio 0.438, truncation yes.

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; skipped "from beginning to end." then continued.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Fail - Ended on "than pronunciation difficulty". Metrics: chars 1266, words 198, duration 97.1s, ratio 1.063, truncation no.

### L008. Generated length sweep ~1400 chars

```text
This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit. This text continues in a calm descriptive style so duration and truncation can be compared against the reported input word count. This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit. This text continues in a calm descriptive style so duration and truncation can be compared against the reported input word count. This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit.
```

Findings
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Fail. Metrics: chars 1489, words 234, duration 40.0s, ratio 0.370, truncation yes.

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; started to slow, then skipped "emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit." Said "pronunciation" as "pronucian," then later skipped "A stable result should keep the same pacing, emphasis, and sentence order from beginning to end."


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Fail. Metrics: chars 1489, words 234, duration 114.7s, ratio 1.062, truncation no.

### L009. Generated length sweep ~1550 chars

```text
This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit. This text continues in a calm descriptive style so duration and truncation can be compared against the reported input word count. This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit. This text continues in a calm descriptive style so duration and truncation can be compared against the reported input word count. This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit. This text continues in a calm descriptive style so duration and truncation can be compared against the reported input word count.
```

Findings
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Fail. Metrics: chars 1619, words 255, duration 40.0s, ratio 0.340, truncation yes.

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): Fail; skipped a large middle span, drew out "or loose structure," then resumed with "This Text is part of ..."


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Fail. Metrics: chars 1619, words 255, duration 124.9s, ratio 1.061, truncation no.

### L010. Generated length sweep ~1700 chars

```text
This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit. This text continues in a calm descriptive style so duration and truncation can be compared against the reported input word count. This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit. This text continues in a calm descriptive style so duration and truncation can be compared against the reported input word count. This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty. A stable result should keep the same pacing, emphasis, and sentence order from beginning to end. If the model begins to rush, clip endings, repeat fragments, or lose structure, that usually marks the practical chunk limit. This text continues in a calm descriptive style so duration and truncation can be compared against the reported input word count. This passage is part of a length stability benchmark for narrated speech. The wording is intentionally plain so the main variable is sequence length rather than pronunciation difficulty.
```

Findings
	Benchmark script 2026-04-06 (`chatterbox_full`, `reference:M_Narrator`, cold cache): Fail. Metrics: chars 1806, words 283, duration 40.0s, ratio 0.306, truncation yes.

Findings CosyVoice-vLLM
	Benchmark script 2026-04-06 (`cosyvoice_vllm`, `reference:M_Narrator`, cold cache): DNF.


Findings Longcat
	Benchmark script 2026-04-08 (`longcat`, `reference:M_Narrator`, cold cache): Fail. Metrics: chars 1806, words 283, duration 138.6s, ratio 1.061, truncation no.

## Practical chunking rule to derive

After testing, you’ll usually end up with a rule like:

- max 3 to 5 sentences per chunk for narration
- max 1 long sentence or 2 medium sentences
- split lists every 8 to 12 items 
- split number-word counts every 10 items
- prefer period breaks over comma-only chains
- use paragraph breaks for section changes
    

For TTS systems, “safe chunk size” is usually not the token limit. It is the point where prosody and sequence tracking begin to degrade.

If you want, I can turn this into a tighter benchmark sheet with 20 numbered tests and a simple scoring rubric so you can log failures consistently.


Maintainer note for future updates:

This file is the canonical human-and-machine-readable provider test record.

When new benchmark results are added, merge them directly into the matching test item under that test’s existing Findings section for the provider. Do not create a separate benchmark-results appendix or summary section for normal updates.

Intent:
- Each test section should remain self-contained.
- A human should be able to read one test and immediately see the prompt, prior findings, and newest findings together.
- A machine or LLM should be able to parse one test block and recover the full history for that specific test without chasing a second section elsewhere in the file.

Update rules:
- Preserve existing findings unless they are clearly wrong and being explicitly corrected.
- Add new results as additional dated or clearly labeled findings under the same provider heading for that test.
- If benchmark script metrics are available, include the important ones inline in concise form rather than moving them to a separate table elsewhere.
- Generated limit tests such as L001-L010 are real test items and should be maintained the same way as the named tests.
- Keep test IDs stable. Do not rename test codes once they exist.
- If the benchmark corpus changes, update the matching test entries in this file rather than creating a disconnected replacement section.

In short: integrate new evidence into the existing per-test findings blocks; do not split the record across multiple sections.