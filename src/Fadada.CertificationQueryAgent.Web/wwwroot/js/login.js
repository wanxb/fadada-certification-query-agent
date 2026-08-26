// Submits credentials with the server-issued antiforgery token and never persists the password.
const form = document.querySelector("[data-login-form]");

if (form) {
    form.addEventListener("submit", async (event) => {
        event.preventDefault();
        const submit = form.querySelector("button[type='submit']");
        const error = document.querySelector(".login-error");
        const data = new FormData(form);
        const token = data.get("__RequestVerificationToken");

        submit.disabled = true;
        submit.textContent = "正在登录…";
        error.hidden = true;

        try {
            const response = await fetch(form.action, {
                method: "POST",
                body: data,
                credentials: "same-origin",
                headers: {
                    "Accept": "application/json",
                    "X-CSRF-TOKEN": token
                }
            });

            if (response.ok) {
                window.location.assign("/");
                return;
            }

            error.textContent = response.status === 429
                ? "登录尝试过于频繁，请稍后再试。"
                : "登录失败，请检查账号、密码或账号状态。";
            error.hidden = false;
        } catch {
            error.textContent = "暂时无法连接服务，请稍后再试。";
            error.hidden = false;
        } finally {
            submit.disabled = false;
            submit.textContent = "登录";
        }
    });
}
