---
paths:
  - "Jint/**/*.cs"
---

This rule exists because the file it points at would otherwise never fire. Jint parses number text, classifies white space, sorts with comparers that script supplies and hands script text to BCL parsers in many places, and in each of them the BCL's answer is not ECMAScript's.

**Read [`Jint/Extensions/AGENTS.md`](../../Jint/Extensions/AGENTS.md) before you edit such a call site.** It is not repeated here or in the repository-root
`AGENTS.md`; that file's index says what each co-located instruction file covers.
