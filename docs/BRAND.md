# EasyShare visual system

The EasyShare mark combines the two product ideas that must remain immediately
recognizable at Windows icon sizes: a folder and a three-node sharing graph.

## Core palette

| Token | Value | Use |
| --- | --- | --- |
| EasyShare Orange | `#F97316` | Brand mark, emphasis and selected accents |
| EasyShare Graphite | `#1F2937` | Light-theme wordmark and mark detail |
| System accent | Windows theme resource | Interactive controls and focus states |

The application UI must use WinUI theme resources for surfaces, text, focus and
contrast. Brand colors do not replace system colors for status or accessibility.

## Mark rules

- Use the full two-color mark at 24 px and above.
- Use the monochrome light/dark variants when the background would hide one of
  the two brand colors.
- Keep clear space equal to at least one share-node radius around the mark.
- Do not add gradients, shadows, bevels or outlines.
- Do not recolor status icons with the brand orange; status keeps its semantic
  system color.

## Application assets

`tools/Generate-BrandAssets.py` deterministically derives MSIX, Store, splash,
tray and ICO assets from `Assets/Brand/EasyShareMark-v2.png`. Regenerate the
family after changing the master and validate 100%, 150% and 200% scaling in
light, dark and contrast themes.

The source concept was generated with the built-in image generation workflow,
then converted to a transparent, fixed two-color master so published assets are
repeatable.
