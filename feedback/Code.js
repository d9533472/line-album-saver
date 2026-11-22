// LINE 相簿批次儲存：接收程式裡「意見回饋」視窗送來的內容，轉寄到作者信箱。
// 部署成網頁應用程式（以自己的身分執行、所有人皆可存取），網址填到 LineAlbumSaver.cs 的 FeedbackUrl。
const TO = 'paul.tong@nmdc-group.com';
const MAX_PER_10_MIN = 20;

function doPost(e) {
  try {
    const p = (e && e.parameter) || {};
    const message = String(p.message || '').trim().slice(0, 5000);
    if (!message) return json({ ok: false, error: '內容是空的' });

    // 簡單防灌：10 分鐘內最多 MAX_PER_10_MIN 封
    const cache = CacheService.getScriptCache();
    const count = Number(cache.get('count') || 0);
    if (count >= MAX_PER_10_MIN) return json({ ok: false, error: '目前回饋太多，請稍後再試' });
    cache.put('count', String(count + 1), 600);

    const kind = String(p.kind || '其他').slice(0, 20);
    const contact = String(p.contact || '').trim().slice(0, 200);
    const log = String(p.log || '').slice(0, 20000);
    const body = [
      '類型：' + kind,
      '聯絡方式：' + (contact || '（未填）'),
      '程式版本：' + String(p.version || '').slice(0, 100),
      '系統：' + String(p.os || '').slice(0, 200),
      '',
      '── 內容 ──',
      message,
      log ? '\n── 執行紀錄 ──\n' + log : ''
    ].join('\n');

    const options = { name: 'LINE 相簿批次儲存' };
    if (/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(contact)) options.replyTo = contact;
    MailApp.sendEmail(TO, '[LINE 相簿批次儲存] ' + kind + '：' + message.split('\n')[0].slice(0, 40), body, options);
    return json({ ok: true });
  } catch (err) {
    return json({ ok: false, error: String(err && err.message || err) });
  }
}

function doGet() { return json({ ok: true, service: 'LineAlbumSaver feedback' }); }

// 第一次部署後在編輯器執行一次，授權寄信權限。
function authorize() { Logger.log('今日剩餘可寄：' + MailApp.getRemainingDailyQuota()); }

function json(o) { return ContentService.createTextOutput(JSON.stringify(o)).setMimeType(ContentService.MimeType.JSON); }
