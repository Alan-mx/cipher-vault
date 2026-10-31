import { createApp } from "vue";
import { createPinia } from "pinia";
import App from "./App.vue";
import { router } from "./router";
import { installSessionLifecycle } from "./session/install";
import "./styles/base.css";

const pinia = createPinia();
const app = createApp(App);
app.use(pinia);
installSessionLifecycle(router, pinia);
app.use(router);
app.mount("#app");
