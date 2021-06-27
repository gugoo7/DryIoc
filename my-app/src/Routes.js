import React from 'react';
import { Switch, Route } from 'react-router-dom';

//Components
import Home from './Components/Home/Home';
import Products from './Components/Products/Products';
import Inventaire from './Components/Inventaire/Inventaire';
import Garage from './Components/Garage/Garage';

const Routes = () => {
    return(
        <Switch>
            <Route exact path='/' component ={Home} />
            <Route exact path='/products' component ={Products} />
            <Route exact path='/garage' component ={Garage} />
            <Route exact path='/inventaire' component ={Inventaire} />
        </Switch>
    )
}

export default Routes;