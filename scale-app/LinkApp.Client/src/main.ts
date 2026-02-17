document.querySelector<HTMLButtonElement>('#seedBtn')!.addEventListener('click', async () => {
  const status = document.getElementById('status')!;
  status.innerText = "Seeding... please wait.";
  try {
    const res = await fetch('http://localhost:5082/seed', { method: 'POST' }); // Check your .NET port!
    const msg = await res.text();
    status.innerText = msg;
  } catch (e) {
    status.innerText = "Error: " + e;
  }
});