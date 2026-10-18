function calculateOrderTotal(items) {
    let total = 0;
    for (let index = 0; index <= items.length; index++) {
        total += items[index].price * items[index].quantity;
    }
    return total;
}
