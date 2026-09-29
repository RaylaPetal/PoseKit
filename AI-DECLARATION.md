---
version: 0.1.2
level: copilot
processes:
  design: pair
  implementation: copilot
  testing: none
  documentation: copilot
  review: assist
  deployment: assist
---

This format is based on
[AI-DECLARATION.md](https://ai-declaration.md/en/0.1.2). Please read it to
understand the levels above.

## Notes

I am not a vibe coder. I have been working professionally as a software
developer for 6 years, and I know how every part of this plugin works. PoseKit
was built with AI assistance (Claude), and this document is here to be
transparent about exactly where and how.

AI is a tool here, not the author. I decide what gets built and how it should
behave, I review what is produced, and nothing ships without me understanding
it and testing it in game. Changes go through a spec-driven workflow: a
proposal, a design and a list of requirements are written and reviewed
before any code is written, so the AI works against decisions I made rather
than improvising.

## Design

Pair. Features, behavior and UX are my decisions, based on how I and the
people I play with actually use animations. Claude helped think through edge
cases and write the design documents for each change, which I reviewed and
corrected before implementation.

## Implementation

Copilot. Most of the code was written by Claude from the reviewed specs, in
small, verifiable steps that I approved and reviewed. The tricky parts
(reading game memory, hooks, bone math, the pairing protocol) were worked
through together, and I checked the results in game rather than trusting
them.

## Testing

None. There is no automated test suite. Every feature is tested by hand, in
game, by me and the people I pair with.

## Documentation

Copilot. The README and code comments were drafted by Claude and edited by me.

## Review

Assist. Claude is used to review changes for bugs before release. Every
finding is checked and either fixed or dismissed by me.

## Deployment

Assist. The GitHub release workflows were written with Claude's help. I run
and manage every release myself.
