# browser

The browser town builder is the **Unity WebGL build** of `/unity`. It's the same project and code as
the Switch build, so the browser and the Switch look and behave alike.

- `dist/`: build output (git-ignored). The server serves it at http://localhost:5080.
- Build it with `./browser-dev.sh --build`, or in the editor with **Porchlight > Build > WebGL (browser builder)**.
- The page template is at `unity/Assets/WebGLTemplates/Porchlight/index.html`, because Unity only reads templates from inside the project.

Put any web-only pieces here: a landing page, accounts, a JavaScript bridge to the Unity instance, and so on.
