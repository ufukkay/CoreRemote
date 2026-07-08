// start-console.js
// PM2 ve Windows uyumlu Next.js başlatıcı betiği
process.argv = [process.argv[0], process.argv[1], 'start'];
require('next/dist/bin/next');
