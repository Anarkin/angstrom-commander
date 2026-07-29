import { serverUrl } from "./config";

function App() {
  return (
    <main>
      <h1>Angstrom Commander</h1>
      <p>All your machines, an ångström apart.</p>
      <p>
        Server: <code>{serverUrl}</code>
      </p>
    </main>
  );
}

export default App;
