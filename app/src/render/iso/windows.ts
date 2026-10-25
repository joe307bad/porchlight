// Lit windows at night. The city kits paint windows in pale and mid blues on
// their colourmaps, so a small patch to the building materials' shader picks
// out window-blue texels on walls and, after dark, glows a random share of them warm
// yellow: each window-sized cell of a building's surface (hashed from where it
// is in the world) is lit or not, more often the higher up it is, so towers
// twinkle more than cottages. A few lit windows flicker like a failing bulb
// or a television.

import type { Material, MeshStandardMaterial } from 'three';

/** Shared by every patched material: 0..1 night, and seconds for the flicker. */
export const windowUniforms = { uNight: { value: 0 }, uTime: { value: 0 } };

/** Patch a building material so its windows light up at night (once per material). */
export function lightWindows(material: Material): void {
  const m = material as MeshStandardMaterial;
  if (!m.isMeshStandardMaterial || m.userData.windows) return;
  m.userData.windows = true;
  m.onBeforeCompile = (shader) => {
    shader.uniforms.uNight = windowUniforms.uNight;
    shader.uniforms.uTime = windowUniforms.uTime;
    shader.vertexShader = shader.vertexShader
      .replace('#include <common>', '#include <common>\nvarying vec3 vLitPos;\nvarying float vLitUp;')
      .replace(
        '#include <project_vertex>',
        `#include <project_vertex>
        vec4 litWorld = vec4(transformed, 1.0);
        vec3 litNormal = objectNormal;
        #ifdef USE_INSTANCING
          litWorld = instanceMatrix * litWorld;
          litNormal = mat3(instanceMatrix) * litNormal;
        #endif
        vLitPos = (modelMatrix * litWorld).xyz;
        vLitUp = abs(normalize(mat3(modelMatrix) * litNormal).y);`,
      );
    shader.fragmentShader = shader.fragmentShader
      .replace(
        '#include <common>',
        `#include <common>
        varying vec3 vLitPos;
        varying float vLitUp;
        uniform float uNight;
        uniform float uTime;
        float litHash(vec3 p) { return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453); }`,
      )
      .replace(
        '#include <emissivemap_fragment>',
        `#include <emissivemap_fragment>
        // Only on walls: blue roofs aren't windows.
        if (uNight > 0.01 && vLitUp < 0.35) {
          // Window glass: clearly bluer than it is red or green, and fairly bright.
          float blue = diffuseColor.b - max(diffuseColor.r, diffuseColor.g);
          float glass = smoothstep(0.08, 0.2, blue) * smoothstep(0.35, 0.55, diffuseColor.b);
          if (glass > 0.0) {
            vec3 cell = floor(vLitPos * vec3(3.2, 2.6, 3.2));
            float h = litHash(cell);
            float chance = mix(0.3, 0.72, clamp(vLitPos.y / 3.5, 0.0, 1.0));
            float lit = step(1.0 - chance, h);
            // About one lit window in eight flickers.
            float f = litHash(cell + 17.0);
            if (f < 0.12) {
              float t = uTime * (6.0 + f * 40.0) + h * 50.0;
              float n = fract(sin(floor(t) * 91.7 + h * 13.0) * 4375.5);
              lit *= mix(0.25, 1.0, step(0.3, n)) * (0.85 + 0.15 * sin(t * 3.1));
            }
            vec3 warm = mix(vec3(1.0, 0.72, 0.38), vec3(1.0, 0.86, 0.6), litHash(cell + 3.0));
            totalEmissiveRadiance += warm * glass * lit * uNight * 1.4;
          }
        }`,
      );
  };
  m.customProgramCacheKey = () => 'lit-windows';
  m.needsUpdate = true;
}
