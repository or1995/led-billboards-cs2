# How to use LED Billboards

## 1. Add your media

1. In the main menu or in game, open **Options → LED Screens**.
2. Press **Open media folder**. It opens:
   `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\ModsData\LedScreens\Media`
3. Copy your files into it. Subfolders are fine.
4. Press **Reload media**. In game you can also press **Reload** in any screen's info panel.

| Type | Formats | Notes |
|---|---|---|
| Animation | `.gif` | Each screen can have its own speed and start time. |
| Image | `.png`, `.jpg`, `.jpeg` | Static. Large images are scaled down to *Max GIF resolution*. |
| Video | `.mp4`, `.webm`, `.mov`, `.m4v`, `.avi`, `.wmv`, `.ogv` | Loops and is muted. **MP4 (H.264)** is the safest choice on Windows. |

The built-in **Test pattern** is always available, even with an empty folder.

**Tips**
- Match the screen's shape. Most screens are about **2:1** (wide); portrait screens, totems and posters are taller than wide. Media is stretched to fill rectangular screens. On round screens the middle of the image is cropped to a circle, so it isn't squashed.
- Keep GIFs reasonable. 512 px wide and a few hundred frames is plenty. Bigger files use more video memory.
- **Scale down by default:** files larger than *Max GIF resolution* (default 512 px) are scaled down.

## 2. Place a screen

Open **Landscaping → LED Screens** and pick a prop:

| Prop | Description |
|---|---|
| LED Wall Screen (Small / Medium / Large / Huge) | Flat panels for building walls or the ground |
| LED Poster Screen (Wall), LED Portrait Wall Screen | Tall wall screens |
| LED Ticker Strip (Short / Long) | Long, thin strips for news tickers and shop fronts |
| LED Video Wall (2×1 / 2×2 / 3×2) | Big wall screens made of several large panels |
| LED Facade Screen (3×3 / 5×3 / 6×5), LED Facade Tower (Portrait) | Building-sized screens, up to about 50 × 20 m |
| LED Billboard (Small / Small 2 / Medium / Large / Huge) | Free-standing billboards on legs |
| LED Street Totem (single or double-sided) | A tall street-level screen on a short pole |
| LED Roadside Screen (Large / Huge) | Low screens on poles, for roadsides and car parks |
| LED High-Pole Screen (Medium / Large / Huge) | A single panel raised on pole(s) |
| LED High-Pole Screen (Extra Wide / Giant / Mega / Ribbon / Portrait) | Several panels showing one big image, up to about 42 × 12 m |
| LED High-Pole Screen (…, Double-Sided) | Two screens back to back, showing the same media on both sides |
| LED Round Billboard (Small / Medium / Large) | Round wall screens |
| LED Round Sign on Pole (Medium / Large) | Double-sided round signs on a pole |

Each prop has its own icon in the toolbar showing its shape.

New screens play whatever is set as **Media** in *Options → LED Screens*, or the media you last marked with **Use for new screens**.

## 3. Change what a screen shows

**Click a placed screen.** The info panel gets an **LED Screen** section:

| Control | What it does |
|---|---|
| **Media** | Choose the file this screen shows. Hidden while a playlist is on. |
| **Playlist: Off / In order / Shuffle** | Turn on a playlist for this screen (see below). |
| **Speed** | 0.1× to 4× playback speed for GIFs. Videos always play at normal speed. |
| **Brightness** | 0–300% for this screen, on top of the global brightness. |
| **Sync / Random start** | *Sync* keeps screens showing the same GIF in step. *Random start* offsets this one so neighbouring screens differ. |
| **Use for new screens** | New screens you place will show this media. |
| **Reload / Folder** | Rescan or open the media folder. |

Changes apply immediately and are saved with the city.

## 4. Playlists

1. Set **Playlist** to **In order** or **Shuffle**.
2. Choose **Switch every** (2–120 seconds).
3. Choose what plays:
   - **All media:** everything in the media folder, including files you add later.
   - Or tick individual files (`[x]`) to build a custom list for this screen.
4. In Shuffle mode, **Reshuffle** picks a new random order.

**How it behaves:**
- **Shuffle** plays every item once before repeating and never shows the same item twice in a row. Each screen has its own order.
- **Now playing** shows which file is on the screen.
- **Preloading:** the next item starts loading a few seconds before the switch, so there's no blank gap. Very large GIFs may still flash briefly.
- **Random start** also shifts the switch times, so a row of screens doesn't change all at once.

## 5. Options (Options → LED Screens)

| Option | Description |
|---|---|
| **Media** | What new screens show. |
| **Apply to selected screen** | Older way to set a selected screen's media; the info panel is easier. |
| **Open media folder / Reload media** | Manage your files. |
| **Brightness** | Global brightness for all screens. |
| **Draw distance** | Screens farther than this aren't drawn. |
| **LED pixel effect** | *Off*: smooth image. *Pixelated*: sharp square pixels. *LED dots*: each pixel is a round glowing dot with dark gaps, like a real LED screen up close, blending into a smooth image from a distance. Media reloads automatically when you change it. |
| **LED resolution** | How many LEDs across the media's width (32–320). Lower means bigger, chunkier pixels. LED dots use about 4× more video memory per GIF than *Off* at the same resolution. |
| **Pole style** | *Auto*: steel poles for medium/large, concrete columns for huge and bigger. *Steel* or *Concrete* forces one style. **Needs a game restart.** |
| **Max GIF resolution / Max frames per GIF** | Limit memory use. Press *Reload media* after changing. |
| **Compress textures** | Uses about 4× less video memory at a small quality cost. |
| **Screen alignment (debug)** | Adjust which side the image is drawn on, how far it sits in front of the panel, and the border. Only needed if an image looks misplaced. |

## Troubleshooting

| Problem | What to do |
|---|---|
| Screen shows grey static ("no signal") | The file is still loading, is missing, or failed to load. Check `Logs\LedScreens.Mod.log`. |
| A video never plays | Convert it to MP4 (H.264). The log shows `Video '…' failed: …`. |
| A video stutters or freezes | The mod skips past stalls automatically. For smooth playback, re-encode the video as *H.264 baseline* (command below). |
| No **LED Screen** section when clicking a screen | `Logs\Modding.log` should contain `Registered UI Module … LedScreens`. If not, reinstall the mod. |
| Can't find the LED Screens tab | It's inside **Landscaping**, and Landscaping must be unlocked in your city. |
| Something else | Open an issue with a screenshot and `Logs\LedScreens.Mod.log`. |

Logs are in `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs`.

### Re-encoding videos for smooth playback

Unity's video decoder on Windows can stall on H.264 files that use B-frames, the default for most downloaded videos. Re-encoding them with [ffmpeg](https://ffmpeg.org/) as *baseline* H.264, without audio, makes them play smoothly and saves space:

```
ffmpeg -i input.mp4 -c:v libx264 -profile:v baseline -level 3.1 -pix_fmt yuv420p -vf "scale=640:-2" -an -movflags +faststart output.mp4
```

- `scale=640:-2` makes the video 640 px wide, plenty for an LED screen. Use `scale=1024:-2` for the biggest screens.
- `-an` removes the audio track, which screens don't play anyway.
