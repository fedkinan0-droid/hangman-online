const alphabet = "абвгдежзийклмнопрстуфхцчшщъыьэюя";
const lobby = document.querySelector("#lobby");
const game = document.querySelector("#game");
const statusText = document.querySelector("#status");
const keyboard = document.querySelector("#keyboard");
let connection;
let latestState = null;

function showMessage(text, error=false) {
  const el = document.querySelector("#lobbyMessage");
  el.textContent = text;
  el.classList.toggle("error", error);
}
async function connect() {
  if (connection && connection.state === signalR.HubConnectionState.Connected) return true;
  connection = new signalR.HubConnectionBuilder().withUrl("/gameHub").withAutomaticReconnect().build();
  connection.on("GameState", renderState);
  try { await connection.start(); return true; }
  catch (e) { showMessage("Не удалось подключиться к серверу. Обнови страницу и попробуй снова.", true); return false; }
}
function playerName() {
  return document.querySelector("#name").value.trim() || "Игрок";
}
document.querySelector("#create").onclick = async () => {
  if (!await connect()) return;
  const code = await connection.invoke("CreateRoom", playerName());
  lobby.classList.add("hidden"); game.classList.remove("hidden");
  document.querySelector("#codeLabel").textContent = code;
};
document.querySelector("#join").onclick = async () => {
  if (!await connect()) return;
  const code = document.querySelector("#roomCode").value.trim().toUpperCase();
  if (code.length !== 6) { showMessage("Введи шестизначный код комнаты.", true); return; }
  const ok = await connection.invoke("JoinRoom", code, playerName());
  if (!ok) { showMessage("Комната не найдена или в ней уже два игрока.", true); return; }
  lobby.classList.add("hidden"); game.classList.remove("hidden");
  document.querySelector("#codeLabel").textContent = code;
};
document.querySelector("#copy").onclick = async () => {
  try { await navigator.clipboard.writeText(document.querySelector("#codeLabel").textContent); document.querySelector("#copy").textContent = "Скопировано!"; }
  catch { alert("Код комнаты: " + document.querySelector("#codeLabel").textContent); }
};
document.querySelector("#back").onclick = () => location.reload();

function renderState(s) {
  latestState = s;
  document.querySelector("#codeLabel").textContent = s.code;
  document.querySelector("#myName").textContent = s.name;
  document.querySelector("#opponentName").textContent = s.opponentName;
  document.querySelector("#myScore").textContent = s.selfScore;
  document.querySelector("#opponentScore").textContent = s.opponentScore;
  document.querySelector("#word").textContent = s.mask;
  document.querySelector("#wrong").textContent = `Ошибки: ${s.wrong}/${s.maxWrong}`;
  const m = Math.floor(s.remainingSeconds / 60).toString().padStart(2,"0");
  const sec = (s.remainingSeconds % 60).toString().padStart(2,"0");
  document.querySelector("#timer").textContent = `${m}:${sec}`;
  statusText.textContent = s.status;
  keyboard.innerHTML = "";
  for (const c of alphabet) {
    const btn = document.createElement("button");
    btn.textContent = c.toUpperCase();
    btn.disabled = !s.started || s.finished || s.failed || s.guessed.includes(c);
    if (s.guessed.includes(c)) btn.classList.add(s.mask.includes(c) ? "good" : "bad");
    btn.onclick = () => connection.invoke("Guess", c);
    keyboard.appendChild(btn);
  }
}
