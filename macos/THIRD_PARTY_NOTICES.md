# Third-party notices

## ECDICT

The embedded offline English–Chinese core dictionary is derived from the
[ECDICT project](https://github.com/skywind3000/ECDICT), downloaded and filtered
on 2026-07-22. The source repository describes ECDICT as a free English-to-Chinese
dictionary database and publishes it under the MIT License. The license text is
included in `licenses/ECDICT_LICENSE.txt`.

This application embeds a filtered core subset selected from ECDICT frequency,
Oxford/core-tag, and Collins-rank metadata, plus a separately maintained set of
Singapore terms and plain-English function-word explanations. No ECDICT audio
URLs or online services are used.

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
