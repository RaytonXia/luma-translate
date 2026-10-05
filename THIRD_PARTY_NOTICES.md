# Third-party notices

## ECDICT

The embedded offline English–Chinese dictionary is derived from the
[ECDICT project](https://github.com/skywind3000/ECDICT), pinned at commit
`bc015ed2e24a7abef49fc6dbbb7fe32c1dadaf8b`. The MIT license text is included
in `licenses/ECDICT_LICENSE.txt` and in macOS application resources.

This build retains 767,778 eligible Chinese entries, with 34 separately authored
academic/phrase supplements and the existing Singapore overlay. The examples
in the supplement are original illustrative examples, not quotations or research
findings. Counts, provenance and hashes are in `source/data/dictionary-manifest.json`.
No ECDICT audio URLs or online services are used.

ECDICT's project history states that its entries aggregate several earlier word
lists, open dictionaries, web-collected material, and community contributions.
Before broad commercial redistribution, the distributor should independently
review the provenance and licensing of the dictionary content for its intended
jurisdiction and use.

## Windows local OCR and speech

Screen text recognition uses the `Windows.Media.Ocr` component and English OCR
language data already installed with Windows. English pronunciation uses the
Windows system speech synthesizer and an installed English voice. These Windows
components and language/voice data are not redistributed in this package and
remain subject to the Windows terms that apply on the user's device.

## Optional cloud API integrations

The application contains optional client integrations for Google Gemini and
DeepSeek. Neither service, SDK, model, nor API key is bundled with this package.
The user must provide a key and explicitly initiate each cloud-assisted usage
request. Those services remain subject to their respective provider terms,
privacy policies, regional availability, quotas, and pricing.
