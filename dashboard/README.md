# لوحة تحكم الرخص

ترفع هذا المجلد إلى سيرفرك فتعمل اللوحة مباشرة. الملفات مربوطة مسبقاً بمشروع Supabase، ولا تحتوي على أي مفتاح سري، ولا تحتاج إلى قاعدة بيانات على سيرفرك.

**يجب تشغيلها عبر HTTPS،** لأنك تدخل فيها كلمة مرور المدير.

اختر الطريقة التي تناسب سيرفرك.

## الطريقة الموصى بها: على سيرفرك بعنوان IP

تعمل دائماً على `https://SERVER_IP:8443`، وتفتحها من أي جهاز، دون نطاق. السكربت جاهز في `release\tahakkum-dashboard-install.sh`، ويعمل على أنظمة عائلة Debian (Ubuntu وDebian وKali). افتح المنفذين 8443 و80 في جدار حماية مزوّد السيرفر. يستخدم المنفذ 80 لإصدار شهادة موثوقة من Let's Encrypt، وبدونه يعمل بشهادة خاصة يحذر منها المتصفح.

من ويندوز، انسخه إلى السيرفر:

```bash
scp release\tahakkum-dashboard-install.sh root@SERVER_IP:/root/
```

ثم على السيرفر:

```bash
bash /root/tahakkum-dashboard-install.sh
```

في النهاية يطبع السكربت رابط اللوحة. وإذا تكرر استخدام `HOST` في الطريقة 3 لفتحها خارج السيرفر، فيجب ضبط `TLS_CERT` و`TLS_KEY`، وإلا يرفض الخادم العمل.

## الطريقة 1: استضافة cPanel أو Apache

1. فك الضغط وارفع كل الملفات إلى `public_html` أو مجلد نطاق فرعي، مع الملف المخفي `.htaccess`.
2. افتح رابط موقعك، وستظهر شاشة الدخول.

## الطريقة 2: سيرفر Linux عليه Nginx

```bash
sudo mkdir -p /var/www/tahakkum-dashboard
```

```bash
sudo unzip tahakkum-dashboard.zip -d /var/www/tahakkum-dashboard
```

```bash
sudo cp /var/www/tahakkum-dashboard/nginx-example.conf /etc/nginx/sites-available/tahakkum-dashboard
```

عدّل النطاق داخل الملف إلى نطاقك، ثم:

```bash
sudo ln -s /etc/nginx/sites-available/tahakkum-dashboard /etc/nginx/sites-enabled/
```

```bash
sudo nginx -t && sudo systemctl reload nginx
```

```bash
sudo certbot --nginx -d YOUR-DOMAIN
```

## الطريقة 3: تشغيلها بـ Node.js

تحتاج Node.js 18 أو أحدث، ولا شيء آخر. من داخل المجلد:

```bash
node server.mjs
```

تعمل على `http://127.0.0.1:8080`، أي على السيرفر نفسه فقط. لتغيير المنفذ والعنوان:

```bash
PORT=3000 HOST=0.0.0.0 node server.mjs
```

لفتحها للعامة ضع أمامها Nginx أو Caddy مع HTTPS، ولا تفتحها مباشرة بدون HTTPS.

لتبقى شغالة بعد إغلاق الطرفية وبعد إعادة تشغيل السيرفر:

```bash
sudo npm install -g pm2
```

```bash
pm2 start server.mjs --name tahakkum-dashboard
```

```bash
pm2 save && pm2 startup
```

## أول دخول

1. سجّل الدخول ببريد وكلمة مرور حساب المدير الذي أنشأته في Supabase.
2. امسح رمز QR بتطبيق مصادقة مثل Google Authenticator، ثم أدخل الرمز.
3. أضف شركة من صفحة "الشركات"، ثم أصدر الأكواد من صفحة "الدفعات".

## ربطها بمشروع Supabase آخر

افتح `config.js` وغيّر الرابط والمفتاح العام (`sb_publishable_`). لا تضع المفتاح السري (`sb_secret_`) هنا أبداً.
