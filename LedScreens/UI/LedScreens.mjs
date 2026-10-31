/*
 * Cities: Skylines II UI Module
 *
 * Id: LedScreens
 * Author: LedScreens
 * Version: 0.4.0
 * Dependencies:
 */

// The Dependencies line must stay, even empty: the game passes the parsed list straight to
// AddTags and throws a NullReferenceException at startup when the line is missing.

// Plain ES module, no build step: React and the cs2/* APIs are globals provided by the game UI.
// Renders the "LED Screen" section of the selected object panel; data comes from the C#
// LedScreenSection (props), changes go back through its triggers.

const React = window.React;
const { trigger } = window["cs2/api"];
const { getModule } = window["cs2/modding"];
const { PanelSection, PanelSectionRow, Dropdown, DropdownItem, DropdownToggle, Button } = window["cs2/ui"];
const h = React.createElement;

const GROUP = "LedScreens";
const SECTION_TYPE = "LedScreens.Systems.LedScreenSection";
const SECTIONS_MODULE = "game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx";
const TEST_PATTERN = "builtin:testpattern";

function tryGetModule(path, name) {
    try {
        return getModule(path, name);
    } catch (e) {
        return null;
    }
}

function mediaLabel(media) {
    if (!media) return "(none)";
    if (media === TEST_PATTERN) return "Test pattern (built-in)";
    return media;
}

/** Slider that follows the C# value but stays smooth while dragging. */
function ValueSlider({ value, start, end, step, disabled, onChange }) {
    const Slider = tryGetModule("game-ui/common/input/slider/slider.tsx", "Slider");
    const [local, setLocal] = React.useState(value);
    React.useEffect(() => setLocal(value), [value]);

    if (!Slider) return null;
    return h("div", { style: { width: "100%", padding: "4rem 0" } },
        h(Slider, {
            value: local,
            start,
            end,
            gamepadStep: step,
            disabled,
            onChange: (v) => {
                const snapped = Math.round(v / step) * step;
                setLocal(snapped);
                onChange(snapped);
            },
        }));
}

function SmallButton({ label, selected, onSelect, tooltip }) {
    return h(Button, {
        variant: "flat",
        selected,
        tooltipLabel: tooltip,
        onSelect,
        style: { padding: "4rem 10rem", marginRight: "6rem", marginTop: "4rem" },
    }, label);
}

// Per-screen LED effect (C# ScreenEffect); the global setting uses LedEffectMode (Off, Pixelated, LED dots).
const EFFECT_DEFAULT = 0;
const EFFECT_NAMES = ["Smooth", "Pixelated", "LED dots"];
const EFFECT_TOOLTIPS = [
    "Smooth image",
    "Sharp square pixels at the LED resolution",
    "Round glowing dots with dark gaps, like a real LED panel up close",
];

/** LED look of this screen: follow the mod setting, or pick one. */
function EffectControls({ effect, defaultEffect }) {
    const current = effect === EFFECT_DEFAULT ? defaultEffect : effect - 1;
    return h(React.Fragment, null,
        h(PanelSectionRow, {
            left: "LED effect",
            right: effect === EFFECT_DEFAULT ? `${EFFECT_NAMES[current]} (default)` : EFFECT_NAMES[current],
            subRow: true,
        }),
        ButtonRow(
            h(SmallButton, {
                label: "Default",
                selected: effect === EFFECT_DEFAULT,
                tooltip: `Follow the LED effect in the mod settings (now: ${EFFECT_NAMES[defaultEffect]})`,
                onSelect: () => trigger(GROUP, "setEffect", EFFECT_DEFAULT),
            }),
            ...EFFECT_NAMES.map((name, i) => h(SmallButton, {
                key: name,
                label: name,
                selected: effect === i + 1,
                tooltip: EFFECT_TOOLTIPS[i],
                onSelect: () => trigger(GROUP, "setEffect", i + 1),
            }))));
}

const PLAYLIST_OFF = 0;
const PLAYLIST_IN_ORDER = 1;
const PLAYLIST_SHUFFLE = 2;

function ButtonRow(...children) {
    return h("div", { style: { display: "flex", flexWrap: "wrap", padding: "2rem 0 6rem 0" } }, ...children);
}

/** Playlist controls: mode, switch interval, and which media take part. */
function PlaylistControls({ playlistMode, interval, playlist, available, nowPlaying }) {
    const { Scrollable } = window["cs2/ui"];
    const ticked = new Set(playlist || []);
    const playsAll = ticked.size === 0;
    const media = (available || []).filter((name) => name !== TEST_PATTERN);

    const modeButtons = ButtonRow(
        h(SmallButton, { label: "Off", selected: playlistMode === PLAYLIST_OFF, onSelect: () => trigger(GROUP, "setPlaylistMode", PLAYLIST_OFF) }),
        h(SmallButton, { label: "In order", selected: playlistMode === PLAYLIST_IN_ORDER, onSelect: () => trigger(GROUP, "setPlaylistMode", PLAYLIST_IN_ORDER) }),
        h(SmallButton, { label: "Shuffle", selected: playlistMode === PLAYLIST_SHUFFLE, onSelect: () => trigger(GROUP, "setPlaylistMode", PLAYLIST_SHUFFLE) }));

    if (playlistMode === PLAYLIST_OFF) {
        return h(React.Fragment, null,
            h(PanelSectionRow, { left: "Playlist", right: "Off", subRow: true }),
            modeButtons);
    }

    const list = h("div", null,
        h(SmallButton, {
            label: "All media",
            selected: playsAll,
            tooltip: "Play everything in the media folder, including files added later",
            onSelect: () => trigger(GROUP, "playAllMedia"),
        }),
        ...media.map((name) => h(SmallButton, {
            key: name,
            label: `${ticked.has(name) ? "[x]" : "[  ]"} ${mediaLabel(name)}`,
            selected: ticked.has(name),
            onSelect: () => trigger(GROUP, "togglePlaylistItem", name),
        })));

    return h(React.Fragment, null,
        h(PanelSectionRow, { left: "Playlist", right: playlistMode === PLAYLIST_SHUFFLE ? "Shuffle" : "In order", subRow: true }),
        modeButtons,
        h(PanelSectionRow, { left: "Now playing", right: mediaLabel(nowPlaying), subRow: true }),
        h(PanelSectionRow, { left: "Switch every", right: `${Math.round(interval)} s`, subRow: true }),
        h(ValueSlider, {
            value: interval, start: 2, end: 120, step: 1,
            onChange: (v) => trigger(GROUP, "setInterval", v),
        }),
        h(PanelSectionRow, { left: "Plays", right: playsAll ? "All media" : `${ticked.size} selected`, subRow: true }),
        Scrollable
            ? h(Scrollable, { style: { maxHeight: "200rem" } }, list)
            : list,
        playlistMode === PLAYLIST_SHUFFLE
            ? ButtonRow(h(SmallButton, {
                label: "Reshuffle",
                tooltip: "Pick a new random order",
                onSelect: () => trigger(GROUP, "reshuffle"),
            }))
            : null);
}

function LedScreenSection(props) {
    const { media, isVideo, speed, brightness, synced, isDefault, available, playlistMode } = props;
    const dropdownTheme = tryGetModule("game-ui/common/input/dropdown/themes/default.module.scss", "classes");
    const playlistOn = playlistMode !== PLAYLIST_OFF;

    const items = (available || []).map((name) =>
        h(DropdownItem, {
            key: name,
            value: name,
            selected: name === media,
            closeOnSelect: true,
            onChange: () => trigger(GROUP, "setMedia", name),
        }, mediaLabel(name)));

    const mediaPicker = h(Dropdown, { theme: dropdownTheme || undefined, content: items },
        h(DropdownToggle, { style: { minWidth: "180rem", maxWidth: "260rem" } }, mediaLabel(media)));

    return h(PanelSection, { tooltip: "Animated LED screen. Put GIF, PNG, JPG or video files in the media folder." },
        h(PanelSectionRow, { uppercase: true, left: "LED Screen" }),
        playlistOn ? null : h(PanelSectionRow, { left: "Media", right: mediaPicker }),
        h(PlaylistControls, props),

        h(PanelSectionRow, {
            left: "Speed",
            right: isVideo ? "Videos play at normal speed" : `${speed.toFixed(1)}x`,
            subRow: true,
        }),
        isVideo ? null : h(ValueSlider, {
            value: speed, start: 0.1, end: 4, step: 0.1,
            onChange: (v) => trigger(GROUP, "setSpeed", v),
        }),

        h(EffectControls, props),

        h(PanelSectionRow, { left: "Brightness", right: `${Math.round(brightness * 100)}%`, subRow: true }),
        h(ValueSlider, {
            value: brightness, start: 0, end: 3, step: 0.05,
            onChange: (v) => trigger(GROUP, "setBrightness", v),
        }),

        h("div", { style: { display: "flex", flexWrap: "wrap", padding: "4rem 0 8rem 0" } },
            isVideo ? null : h(SmallButton, {
                label: "Sync",
                selected: synced,
                tooltip: "Start the animation in step with other screens showing the same GIF",
                onSelect: () => trigger(GROUP, "syncStart"),
            }),
            isVideo ? null : h(SmallButton, {
                label: "Random start",
                selected: !synced,
                tooltip: "Start the animation at a random point, so neighbouring screens differ",
                onSelect: () => trigger(GROUP, "randomStart"),
            }),
            h(SmallButton, {
                label: isDefault ? "Default for new" : "Use for new screens",
                selected: isDefault,
                tooltip: "Newly placed screens will show this media",
                onSelect: () => trigger(GROUP, "useForNewScreens"),
            }),
            h(SmallButton, {
                label: "Reload",
                tooltip: "Reload all media. New and changed files are picked up automatically within a few seconds.",
                onSelect: () => trigger(GROUP, "reloadMedia"),
            }),
            h(SmallButton, {
                label: "Folder",
                tooltip: "Open the media folder",
                onSelect: () => trigger(GROUP, "openMediaFolder"),
            })));
}

export default function register(moduleRegistry) {
    moduleRegistry.extend(SECTIONS_MODULE, "selectedInfoSectionComponents", (components) => {
        components[SECTION_TYPE] = LedScreenSection;
        return components;
    });
}
