// The exact set of shot parameters this runtime implements. Injected into the
// manifest as the camera.params capability, so the Core compiler rejects any
// authored param outside this set (e.g. shake) at validate time instead of the
// engine silently ignoring it. Semantics of each name come from the Core
// contract (gd contract --json, ShotVocabulary.Params); this list is only the
// runtime's implementation coverage. The one consumer is server.mjs (Node);
// runtime.js reads param values generically and must not restate the list.
export const ImplementedShotParams = [
  'targetHeight', 'coverage', 'distance',
  'offsetX', 'offsetY', 'offsetZ',
  'toOffsetX', 'toOffsetY', 'toOffsetZ',
  'orbitDeg', 'tilt', 'pan', 'roll', 'endFov',
];
