class Formatters {
    toSmallDateTime(dStr) {
        const d = new Date(dStr);
        return `${this.pad(d.getMonth()+1, 2)}-${d.getDate()} 
                ${d.getHours()}:${d.getMinutes()}`;
    }
    pad(input, size) {
        let s = `${input}`;
        while (s.length < (size || 2)) {s = "0" + s;}
        return s;
      }
}
export default new Formatters();
